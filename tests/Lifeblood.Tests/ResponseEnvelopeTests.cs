using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Coverage for the v0.6.7 truth-envelope foundation
/// (LB-INBOX-001 + LB-OBS-004). Three concerns:
///
/// 1. The decorator's per-tool classification table is correct and
///    every read-side tool registered in <c>ToolRegistry</c> has an
///    entry (no silent fall-through to the conservative default).
/// 2. The staleness math reads <c>EnvelopeContext.AnalyzedAtUtc</c>
///    and <c>FileSystem.GetLastWriteTimeUtc</c> as documented.
/// 3. End-to-end: every read-side tool response from <c>ToolHandler</c>
///    carries a top-level <c>envelope</c> field (INV-ENVELOPE-001).
/// </summary>
public class ResponseEnvelopeTests
{
    // ──────────────────────────────────────────────────────────────────
    // 1. Classification table
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a decorator wired to ToolRegistry the same way the
    /// production composition root does. Every test that exercises
    /// real per-tool classification routes through this helper so a
    /// single change in the production wiring carries into tests.
    /// </summary>
    private static LifebloodResponseDecorator BuildRegistryDecorator()
    {
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);
        return new LifebloodResponseDecorator(classifications);
    }

    [Fact]
    public void Decorator_KnownTools_DoNotFallBackToConservativeDefault()
    {
        var d = BuildRegistryDecorator();
        // Sanity for a few representative tools; the comprehensive
        // coverage check is the registry ratchet below.
        var lookupEnv = d.Decorate("lifeblood_lookup", new EnvelopeContext());
        Assert.Equal(TruthTier.Semantic, lookupEnv.TruthTier);
        Assert.Equal(ConfidenceBand.Proven, lookupEnv.Confidence);

        var blastEnv = d.Decorate("lifeblood_blast_radius", new EnvelopeContext());
        Assert.Equal(TruthTier.Derived, blastEnv.TruthTier);

        var deadEnv = d.Decorate("lifeblood_dead_code", new EnvelopeContext());
        Assert.Equal(TruthTier.Heuristic, deadEnv.TruthTier);
        Assert.Equal(ConfidenceBand.Advisory, deadEnv.Confidence);
        Assert.NotEmpty(deadEnv.Limitations);
    }

    [Fact]
    public void Decorator_UnknownTool_DegradesToConservativeDefault()
    {
        var d = BuildRegistryDecorator();
        var env = d.Decorate("totally_made_up_tool_name", new EnvelopeContext());
        Assert.Equal(TruthTier.Heuristic, env.TruthTier);
        Assert.Equal(ConfidenceBand.Speculative, env.Confidence);
        Assert.NotEmpty(env.Limitations);
        Assert.Contains("Unregistered tool", env.Limitations[0]);
    }

    [Fact]
    public void Decorator_RoslynFullCapability_LeavesRegistryEnvelopeUnchanged()
    {
        var d = BuildRegistryDecorator();
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AdapterCapability = RoslynCapabilityDescriptor.Capability,
        });

        Assert.Equal(TruthTier.Semantic, env.TruthTier);
        Assert.Equal(ConfidenceBand.Proven, env.Confidence);
        Assert.Equal("Semantic", env.EvidenceSource);
        Assert.Empty(env.Limitations);
    }

    [Fact]
    public void Decorator_NativeClangCapability_AddsAdapterLimitsWithoutDowngradingProvenDirectFacts()
    {
        var d = BuildRegistryDecorator();
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AdapterCapability = new AdapterCapability
            {
                Language = "c",
                AdapterName = "native-clang",
                AdapterVersion = "0.1.0-dev",
                CanDiscoverSymbols = true,
                TypeResolution = ConfidenceLevel.Proven,
                CallResolution = ConfidenceLevel.Proven,
                ImplementationResolution = ConfidenceLevel.None,
                CrossModuleReferences = ConfidenceLevel.None,
                OverrideResolution = ConfidenceLevel.None,
            },
        });

        Assert.Equal(ConfidenceBand.Proven, env.Confidence);
        Assert.Equal("Semantic", env.EvidenceSource);
        Assert.Contains(env.Limitations, l =>
            l.Contains("native-clang", StringComparison.Ordinal) &&
            l.Contains("implementationResolution=None", StringComparison.Ordinal));
    }

    [Fact]
    public void Decorator_BestEffortAdapter_DowngradesProvenToolClassificationToAdvisory()
    {
        var d = BuildRegistryDecorator();
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AdapterCapability = new AdapterCapability
            {
                Language = "python",
                AdapterName = "python-ast",
                AdapterVersion = "0.1.0",
                CanDiscoverSymbols = true,
                TypeResolution = ConfidenceLevel.BestEffort,
                CallResolution = ConfidenceLevel.BestEffort,
                ImplementationResolution = ConfidenceLevel.None,
                CrossModuleReferences = ConfidenceLevel.None,
                OverrideResolution = ConfidenceLevel.None,
            },
        });

        Assert.Equal(ConfidenceBand.Advisory, env.Confidence);
        Assert.Equal("Semantic", env.EvidenceSource);
        Assert.Contains(env.Limitations, l =>
            l.Contains("typeResolution=BestEffort", StringComparison.Ordinal) &&
            l.Contains("callResolution=BestEffort", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolHandler_ImportedNativeGraph_EnvelopeCarriesAdapterCapability()
    {
        var fs = new PhysicalFileSystem();
        var handler = CreateHandler(fs);
        var graphPath = Path.Combine(
            FindRepoRoot(),
            "adapters",
            "native-clang",
            "test-fixtures",
            "tiny-c",
            "expected.graph.json");

        var analyze = handler.Handle("lifeblood_analyze", JsonArgs(new { graphPath }));
        Assert.NotEqual(true, analyze.IsError);
        using var analyzeDoc = JsonDocument.Parse(ExtractText(analyze));
        Assert.Equal(
            "Semantic",
            analyzeDoc.RootElement.GetProperty("envelope").GetProperty("evidenceSource").GetString());
        Assert.Contains(analyzeDoc.RootElement.GetProperty("envelope").GetProperty("limitations").EnumerateArray(), item =>
            item.GetString()!.Contains("native-clang-fixture", StringComparison.Ordinal));

        var lookup = handler.Handle("lifeblood_lookup", JsonArgs(new { symbolId = "method:decode(Packet*)" }));
        Assert.NotEqual(true, lookup.IsError);

        using var doc = JsonDocument.Parse(ExtractText(lookup));
        var envelope = doc.RootElement.GetProperty("envelope");
        Assert.Equal("Semantic", envelope.GetProperty("evidenceSource").GetString());
        Assert.Contains(envelope.GetProperty("limitations").EnumerateArray(), item =>
            item.GetString()!.Contains("native-clang-fixture", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolHandler_ImportedGraphWithoutAdapterMetadata_GetsUnknownAdapterCapability()
    {
        var fs = new PhysicalFileSystem();
        var handler = CreateHandler(fs);
        var graphPath = WriteLegacyGraphWithoutAdapterMetadata();

        try
        {
            var analyze = handler.Handle("lifeblood_analyze", JsonArgs(new { graphPath }));
            Assert.NotEqual(true, analyze.IsError);

            using var doc = JsonDocument.Parse(ExtractText(analyze));
            var envelope = doc.RootElement.GetProperty("envelope");
            Assert.Equal("Advisory", envelope.GetProperty("confidence").GetString());
            Assert.Contains(envelope.GetProperty("limitations").EnumerateArray(), item =>
                item.GetString()!.Contains("unknown-json-graph", StringComparison.Ordinal) &&
                item.GetString()!.Contains("discoverSymbols=False", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(graphPath))
                File.Delete(graphPath);
        }
    }

    /// <summary>
    /// Pinning ratchet — every read-side ToolDefinition in the registry
    /// must declare an EnvelopeClassification. Adding a new read-side
    /// tool without one fails here so INV-ENVELOPE-001 is not silently
    /// weakened. This is the structural guarantee the registry-driven
    /// design buys us: classification cannot drift from the registry
    /// because the registry IS the source.
    /// </summary>
    [Fact]
    public void Registry_EveryReadSideTool_DeclaresEnvelopeClassification()
    {
        var missing = ToolRegistry.GetDefinitions()
            .Where(t => t.Availability == ToolAvailability.ReadSide)
            .Where(t => t.EnvelopeClassification == null)
            .Select(t => t.Name)
            .ToArray();
        Assert.True(missing.Length == 0,
            "Every read-side ToolDefinition must declare EnvelopeClassification (INV-ENVELOPE-001). Missing: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Registry_EveryToolWithEnvelopeWrap_DeclaresEnvelopeClassification()
    {
        // S6 / INV-ADVISORY-LIMITATIONS-001. WrapWriteSide in ToolHandler
        // injects an envelope onto every WriteSide response that produces
        // a JSON-object payload. A WriteSide tool without EnvelopeClassification
        // would fall through to the degraded "Unregistered tool" envelope
        // — silently undermining the contract that classifications are
        // intentional declarations of confidence.
        var missing = ToolRegistry.GetDefinitions()
            .Where(t => t.EnvelopeClassification == null)
            .Select(t => t.Name)
            .ToArray();
        Assert.True(missing.Length == 0,
            "Every ToolDefinition (read OR write side) must declare EnvelopeClassification — write-side responses are now wrapped through WrapWriteSide. Missing: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void Decorator_AllReadSideToolsInRegistry_ResolveToRealClassification()
    {
        var d = BuildRegistryDecorator();
        var ctx = new EnvelopeContext();
        foreach (var t in ToolRegistry.GetDefinitions().Where(t => t.Availability == ToolAvailability.ReadSide))
        {
            var env = d.Decorate(t.Name, ctx);
            // The envelope must reflect the registry's classification
            // exactly — no fallback path may fire for a registered tool.
            Assert.Equal(t.EnvelopeClassification!.TruthTier, env.TruthTier);
            Assert.Equal(t.EnvelopeClassification.Confidence, env.Confidence);
            Assert.Equal(t.EnvelopeClassification.EvidenceSource, env.EvidenceSource);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 2. Staleness computation
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decorator_NoAnalyzedAt_ReturnsZeroStaleness()
    {
        var d = new LifebloodResponseDecorator();
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext());
        Assert.Equal(0, env.StalenessSeconds);
        Assert.Equal(0, env.FilesChangedSinceAnalyze);
    }

    [Fact]
    public void Decorator_AnalyzedAtSetButNoFs_ReturnsSecondsZeroFilesChanged()
    {
        var d = new LifebloodResponseDecorator();
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow.AddSeconds(-90),
        });
        Assert.True(env.StalenessSeconds >= 89);
        Assert.Equal(0, env.FilesChangedSinceAnalyze);
    }

    [Fact]
    public void Decorator_FileNewerThanAnalyzedAt_ReportsStaleFile()
    {
        var d = new LifebloodResponseDecorator();
        var fs = new StubFileSystem(new System.Collections.Generic.Dictionary<string, System.DateTime>
        {
            ["a.cs"] = System.DateTime.UtcNow,                    // brand new
            ["b.cs"] = System.DateTime.UtcNow.AddHours(-1),       // older than analyze
        });
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow.AddMinutes(-30),
            TrackedFilePaths = new[] { "a.cs", "b.cs" },
            FileSystem = fs,
        });
        Assert.Equal(1, env.FilesChangedSinceAnalyze);
    }

    [Fact]
    public void Decorator_FileScanLimit_HonoredOnceDriftFound()
    {
        var d = new LifebloodResponseDecorator();
        var dict = new System.Collections.Generic.Dictionary<string, System.DateTime>(System.StringComparer.Ordinal);
        for (int i = 0; i < 1000; i++) dict[$"file{i}.cs"] = System.DateTime.UtcNow.AddHours(-1);
        dict["file0.cs"] = System.DateTime.UtcNow;
        var fs = new StubFileSystem(dict);
        var env = d.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow.AddMinutes(-30),
            TrackedFilePaths = dict.Keys.ToArray(),
            FileSystem = fs,
            FileScanLimit = 16,
        });
        // Exactly one stale file at index 0 — short-circuit kicks in
        // after the cap is reached AND at least one drift was seen.
        Assert.True(env.FilesChangedSinceAnalyze >= 1);
        Assert.True(fs.StatCount <= 17);
    }

    // ──────────────────────────────────────────────────────────────────
    // 3. End-to-end ToolHandler integration
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolHandler_LookupResponse_CarriesTopLevelEnvelope()
    {
        var handler = HandlerOver(BuildTinyGraph());
        var result = handler.Handle("lifeblood_lookup", MakeArgs(new { symbolId = "type:N.Foo" }));
        Assert.Null(result.IsError);

        var json = result.Content[0].Text;
        Assert.Contains("\"envelope\":", json);
        Assert.Contains("\"truthTier\":", json);
        Assert.Contains("\"confidence\":", json);
        Assert.Contains("\"evidenceSource\":", json);
    }

    [Fact]
    public void ToolHandler_ResolveShortName_CarriesEnvelope()
    {
        var handler = HandlerOver(BuildTinyGraph());
        var result = handler.Handle("lifeblood_resolve_short_name", MakeArgs(new { name = "Foo" }));
        Assert.Null(result.IsError);
        Assert.Contains("\"envelope\":", result.Content[0].Text);
    }

    [Fact]
    public void ToolHandler_BlastRadius_DerivedTier_InEnvelope()
    {
        var handler = HandlerOver(BuildTinyGraph());
        var result = handler.Handle("lifeblood_blast_radius", MakeArgs(new { symbolId = "type:N.Foo" }));
        Assert.Null(result.IsError);
        var json = result.Content[0].Text;
        // ToolHandler.JsonOpts uses JsonStringEnumConverter so the field
        // serializes as a string. Indent setting may insert a space after
        // the colon — accept both variants.
        Assert.True(
            json.Contains("\"truthTier\": \"Derived\"") || json.Contains("\"truthTier\":\"Derived\""),
            "Expected envelope.truthTier to be \"Derived\". Got: " + json.Substring(0, System.Math.Min(800, json.Length)));
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static SemanticGraph BuildTinyGraph()
    {
        var ev = new Evidence
        {
            Kind = EvidenceKind.Semantic,
            AdapterName = "test",
            Confidence = ConfidenceLevel.Proven,
        };
        return new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", QualifiedName = "N.Foo", Kind = SymbolKind.Type, FilePath = "Foo.cs" })
            .AddSymbol(new Symbol { Id = "type:N.Bar", Name = "Bar", QualifiedName = "N.Bar", Kind = SymbolKind.Type, FilePath = "Bar.cs" })
            .AddEdge(new Edge { SourceId = "type:N.Bar", TargetId = "type:N.Foo", Kind = EdgeKind.References, Evidence = ev })
            .Build();
    }

    private static ToolHandler HandlerOver(SemanticGraph graph)
    {
        var fs = new Lifeblood.Adapters.CSharp.PhysicalFileSystem();
        var session = new GraphSession(fs);
        // Inject the graph by going through the JSON-graph path in a
        // temp file would be heavy; instead reach into the session's
        // public Load with an exported graph. For a unit-fast path,
        // load via a serialized in-memory JSON graph.
        var doc = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability { CanDiscoverSymbols = true, TypeResolution = ConfidenceLevel.Proven },
            Graph = graph,
        };
        var tempPath = Path.Combine(Path.GetTempPath(), $"envelope-test-{System.Guid.NewGuid():N}.json");
        using (var s = File.Create(tempPath))
            new Lifeblood.Adapters.JsonGraph.JsonGraphExporter().Export(doc, s);

        IBlastRadiusProvider br = new TinyBlast();
        IMcpGraphProvider provider = new LifebloodMcpProvider(br);
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer dead = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partial = new LifebloodPartialViewBuilder(fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(fs);
        IResponseDecorator decorator = BuildRegistryDecorator();
        var handler = new ToolHandler(session, provider, resolver, search, dead, partial, invariants, decorator);
        handler.Handle("lifeblood_analyze", MakeArgs(new { graphPath = tempPath }));
        return handler;
    }

    private static JsonElement? MakeArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private sealed class TinyBlast : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => Lifeblood.Analysis.BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    // ──────────────────────────────────────────────────────────────────
    // S5: AnalysisGeneration plumbing. Pins the monotonic generation
    // counter from WorkspaceSession.Load through EnvelopeContext to
    // ResponseEnvelope. Cross-tool join coherence depends on this.
    // INV-DIAGNOSE-FRESHNESS-001.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnalysisGeneration_FreshSession_IsZero()
    {
        var session = new Lifeblood.Application.UseCases.WorkspaceSession();
        Assert.Equal(0L, session.AnalysisGeneration);
    }

    [Fact]
    public void AnalysisGeneration_IncrementsOnEveryLoad()
    {
        var session = new Lifeblood.Application.UseCases.WorkspaceSession();
        var emptyGraph = new GraphBuilder().Build();
        var analysis = new AnalysisResult();

        session.Load(emptyGraph, analysis, null, "csharp");
        Assert.Equal(1L, session.AnalysisGeneration);

        session.Load(emptyGraph, analysis, null, "csharp");
        Assert.Equal(2L, session.AnalysisGeneration);

        session.Load(emptyGraph, analysis, null, "csharp");
        Assert.Equal(3L, session.AnalysisGeneration);
    }

    [Fact]
    public void AnalysisGeneration_SurvivesClear_MonotonicAcrossClearLoadPairs()
    {
        // Clear/Load is the auto-refresh path. Two reads taken either
        // side of a Clear/Load pair must see DISTINCT generation values
        // so cross-tool joins can detect that the underlying graph
        // changed.
        var session = new Lifeblood.Application.UseCases.WorkspaceSession();
        var emptyGraph = new GraphBuilder().Build();
        var analysis = new AnalysisResult();

        session.Load(emptyGraph, analysis, null, "csharp");
        var gen1 = session.AnalysisGeneration;

        session.Clear();
        // After Clear: state is empty but the counter is preserved so
        // the next Load produces a strictly-greater value.
        Assert.Equal(gen1, session.AnalysisGeneration);

        session.Load(emptyGraph, analysis, null, "csharp");
        Assert.True(session.AnalysisGeneration > gen1,
            $"Post-Clear-Load generation ({session.AnalysisGeneration}) must exceed pre-Clear ({gen1}).");
    }

    [Fact]
    public void Decorator_CopiesAnalysisGenerationFromContextToEnvelope()
    {
        var d = new LifebloodResponseDecorator(
            new System.Collections.Generic.Dictionary<string, EnvelopeClassification>(System.StringComparer.Ordinal)
            {
                ["any_tool"] = new EnvelopeClassification
                {
                    TruthTier = TruthTier.Semantic,
                    Confidence = ConfidenceBand.Proven,
                },
            });

        var env = d.Decorate("any_tool", new EnvelopeContext { AnalysisGeneration = 42 });
        Assert.Equal(42L, env.AnalysisGeneration);
    }

    [Fact]
    public void Decorator_UnregisteredTool_StillCarriesAnalysisGeneration()
    {
        // The unregistered-tool path returns a conservative envelope.
        // It must still copy the generation so even degraded responses
        // remain joinable by generation. Catch the F3-series mistake
        // class: feature wired on the happy path but forgotten on the
        // fallback.
        var d = new LifebloodResponseDecorator();
        var env = d.Decorate("not_a_real_tool", new EnvelopeContext { AnalysisGeneration = 7 });
        Assert.Equal(7L, env.AnalysisGeneration);
        Assert.Equal(TruthTier.Heuristic, env.TruthTier);
    }

    [Fact]
    public void Decorator_NoWorkspaceLoaded_AnalysisGenerationIsZero()
    {
        var d = new LifebloodResponseDecorator();
        var env = d.Decorate("any_tool", new EnvelopeContext());
        Assert.Equal(0L, env.AnalysisGeneration);
    }

    private static ToolHandler CreateHandler(PhysicalFileSystem fs)
    {
        IMcpGraphProvider provider = new LifebloodMcpProvider(new TestBlastRadiusProvider());
        ISymbolResolver resolver = new LifebloodSymbolResolver();
        ISemanticSearchProvider search = new LifebloodSemanticSearchProvider();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(fs);
        Lifeblood.Application.Ports.Right.Invariants.IInvariantProvider invariants
            = new LifebloodInvariantProvider(fs);
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications);
        return new ToolHandler(new GraphSession(fs), provider, resolver, search, deadCode, partialView, invariants, decorator);
    }

    private sealed class TestBlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(Lifeblood.Domain.Graph.SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private static JsonElement? JsonArgs(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static string ExtractText(McpToolResult result)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Content));
        var text = doc.RootElement[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        return text!;
    }

    private static string FindRepoRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Lifeblood.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate Lifeblood.sln from test base directory.");
    }

    private static string WriteLegacyGraphWithoutAdapterMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lifeblood-legacy-graph-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
{
  "version": "1.0",
  "language": "c",
  "symbols": [
    { "id": "mod:legacy", "name": "legacy", "kind": "module" }
  ],
  "edges": []
}
""");
        return path;
    }

    private sealed class StubFileSystem : Lifeblood.Application.Ports.Infrastructure.IFileSystem
    {
        private readonly System.Collections.Generic.Dictionary<string, System.DateTime> _mtimes;
        public int StatCount { get; private set; }
        public StubFileSystem(System.Collections.Generic.Dictionary<string, System.DateTime> mtimes) => _mtimes = mtimes;
        public string ReadAllText(string path) => "";
        public System.Collections.Generic.IEnumerable<string> ReadLines(string path) => System.Array.Empty<string>();
        public Stream OpenRead(string path) => Stream.Null;
        public Stream OpenWrite(string path) => Stream.Null;
        public bool FileExists(string path) => _mtimes.ContainsKey(path);
        public bool DirectoryExists(string path) => false;
        public string[] FindFiles(string directory, string pattern, bool recursive = true) => System.Array.Empty<string>();
        public System.DateTime GetLastWriteTimeUtc(string path)
        {
            StatCount++;
            return _mtimes.TryGetValue(path, out var t) ? t : System.DateTime.MinValue;
        }
    }
}
