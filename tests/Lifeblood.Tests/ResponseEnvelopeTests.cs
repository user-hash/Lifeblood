using System.Text.Json;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
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

    [Fact]
    public void Decorator_KnownTools_DoNotFallBackToConservativeDefault()
    {
        var d = new LifebloodResponseDecorator();
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
        var d = new LifebloodResponseDecorator();
        var env = d.Decorate("totally_made_up_tool_name", new EnvelopeContext());
        Assert.Equal(TruthTier.Heuristic, env.TruthTier);
        Assert.Equal(ConfidenceBand.Speculative, env.Confidence);
        Assert.NotEmpty(env.Limitations);
        Assert.Contains("Unregistered tool", env.Limitations[0]);
    }

    /// <summary>
    /// Pinning ratchet — every read-side tool advertised by
    /// <c>ToolRegistry</c> must have a classification entry in the
    /// decorator. Adding a new read-side tool without an entry fails
    /// here (the unregistered-tool fallback would still ship a usable
    /// response, but we deliberately make this a hard fail so
    /// INV-ENVELOPE-001 is not silently weakened).
    /// </summary>
    [Fact]
    public void Decorator_AllReadSideToolsInRegistry_HaveClassification()
    {
        var d = new LifebloodResponseDecorator();
        var ctx = new EnvelopeContext();
        var readSide = ToolRegistry.GetDefinitions()
            .Where(t => t.Availability == ToolAvailability.ReadSide)
            .Select(t => t.Name)
            .ToArray();
        Assert.NotEmpty(readSide);
        foreach (var tool in readSide)
        {
            var env = d.Decorate(tool, ctx);
            Assert.True(
                env.Confidence != ConfidenceBand.Speculative
                || env.Limitations.Length == 0
                || !env.Limitations[0].Contains("Unregistered tool"),
                $"Read-side tool '{tool}' has no decorator classification — add an entry to LifebloodResponseDecorator.Classifications.");
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
        IResponseDecorator decorator = new LifebloodResponseDecorator();
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

    private sealed class StubFileSystem : Lifeblood.Application.Ports.Infrastructure.IFileSystem
    {
        private readonly System.Collections.Generic.Dictionary<string, System.DateTime> _mtimes;
        public int StatCount { get; private set; }
        public StubFileSystem(System.Collections.Generic.Dictionary<string, System.DateTime> mtimes) => _mtimes = mtimes;
        public string ReadAllText(string path) => "";
        public System.Collections.Generic.IEnumerable<string> ReadLines(string path) => System.Array.Empty<string>();
        public Stream OpenRead(string path) => Stream.Null;
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
