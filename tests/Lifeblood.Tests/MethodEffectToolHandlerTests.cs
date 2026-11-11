using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class MethodEffectToolHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lifeblood-method-effects-{Guid.NewGuid():N}");
    private readonly PhysicalFileSystem _fs = new();

    public MethodEffectToolHandlerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Effects.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Effects.cs"), """
            using System;
            namespace Effects
            {
                public sealed class Host
                {
                    private int _state;
                    public void Sink() { _state++; }
                    public void Run() { Action callback = () => Sink(); }
                }
            }
            """);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void MethodEffectsUsesOnePublicationAndLegacyProjectionKeepsOpaqueCallableKeys(CanonicalSymbolIdVersion policy)
    {
        using var session = new GraphSession(_fs, canonicalIdVersion: policy);
        session.Load(_root, graphPath: null, rulesPath: null, readOnly: false);
        var handler = CreateHandler(session);
        var before = session.CurrentSnapshot;
        var provider = session.OperationFactProvider;
        var root = session.Graph!.Symbols.Single(symbol => symbol.Name == "Run").Id;
        var result = handler.Handle("lifeblood_method_effects", Args(new { symbolIds = new[] { root } }));
        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(result.Content[0].Text);
        var report = json.RootElement;
        Assert.Equal("lifeblood.method_effects", report.GetProperty("kind").GetString());
        var method = Assert.Single(report.GetProperty("methods").EnumerateArray());
        Assert.Equal(0, method.GetProperty("callCount").GetInt32());
        Assert.Equal(1, method.GetProperty("deferredCallableCount").GetInt32());
        Assert.Equal(0, report.GetProperty("scanReceipt").GetProperty("additionalSemanticBaseCount").GetInt32());
        Assert.Equal("Advisory", report.GetProperty("envelope").GetProperty("confidence").GetString());
        var legacy = Assert.IsType<McpToolResult>(result.LegacyProjection);
        Assert.NotEqual(true, legacy.IsError);
        using var oldJson = JsonDocument.Parse(legacy.Content[0].Text);
        var oldMethod = Assert.Single(oldJson.RootElement.GetProperty("methods").EnumerateArray());
        Assert.Equal(method.GetProperty("rootCallableId").GetString(), oldMethod.GetProperty("rootCallableId").GetString());
        Assert.Equal(method.GetProperty("callbackBindings").GetRawText(), oldMethod.GetProperty("callbackBindings").GetRawText());
        Assert.Equal("method:Effects.Host.Run()", oldMethod.GetProperty("rootSymbolId").GetString());
        Assert.Same(before, session.CurrentSnapshot);
        Assert.Same(provider, session.OperationFactProvider);

        var batch = handler.Handle("lifeblood_batch", Args(new
        {
            responseFormat = "compact",
            calls = new[] { new { tool = "lifeblood_method_effects", arguments = new { symbolIds = new[] { root }, maxEvidencePerMethod = 0 } } },
        }));
        Assert.NotEqual(true, batch.IsError);
        using var batchJson = JsonDocument.Parse(batch.Content[0].Text);
        var child = Assert.Single(batchJson.RootElement.GetProperty("results").EnumerateArray());
        Assert.False(child.GetProperty("isError").GetBoolean());
        Assert.Equal("lifeblood.method_effects", child.GetProperty("payload").GetProperty("kind").GetString());
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Fact]
    public void InvalidRootsBudgetsProfilesAndPublicationPreconditionsFailWithoutReplacingThePublication()
    {
        using var session = new GraphSession(_fs);
        var handler = CreateHandler(session);
        Assert.True(handler.Handle("lifeblood_method_effects", Args(new { symbolIds = new[] { "missing" } })).IsError);
        session.Load(_root, graphPath: null, rulesPath: null, readOnly: false);
        var before = session.CurrentSnapshot;
        var root = session.Graph!.Symbols.Single(symbol => symbol.Name == "Run").Id;
        foreach (var request in new object[]
        {
            new { symbolIds = Array.Empty<string>() }, new { symbolIds = new[] { root, "" } },
            new { symbolIds = new[] { root, root } }, new { symbolIds = new[] { "missing" } },
            new { symbolIds = new[] { root }, maxFacts = 0 }, new { symbolIds = new[] { root }, maxDepth = 9 },
            new { symbolIds = new[] { root }, maxFacts = long.MaxValue }, new { symbolIds = new[] { root }, profileScope = "unknown" },
            new { symbolIds = new[] { root }, expectedSnapshotId = "snap_00000000000000000000000000000000" },
        })
        {
            var result = handler.Handle("lifeblood_method_effects", Args(request));
            Assert.True(result.IsError, result.Content[0].Text);
            Assert.Same(before, session.CurrentSnapshot);
        }
    }

    private ToolHandler CreateHandler(GraphSession session)
        => new(session, new LifebloodMcpProvider(new BlastRadius()), new LifebloodSymbolResolver(),
            new LifebloodSemanticSearchProvider(), new LifebloodDeadCodeAnalyzer(), new LifebloodPartialViewBuilder(_fs),
            new LifebloodInvariantProvider(_fs), new LifebloodResponseDecorator(ToolRegistry.GetDefinitions()
                .Where(definition => definition.EnvelopeClassification != null)
                .ToDictionary(definition => definition.Name, definition => definition.EnvelopeClassification!, StringComparer.Ordinal)));

    [Fact]
    public void SecondaryProfileDeclarationsAndEffectsAreEphemeralAndInputDriftRejectsBeforeFacts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Library"));
        File.WriteAllText(Path.Combine(_root, "Effects.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup></Project>
            """);
        var sourcePath = Path.Combine(_root, "Effects.cs");
        File.WriteAllText(sourcePath, """
            using System;
            namespace Effects
            {
                public sealed class Host
                {
                    private int _state;
                    public void Sink() { _state++; }
                    public void Run()
                    {
            #if UNITY_EDITOR
                        Action callback = () => Sink();
            #else
                        Sink();
            #endif
                    }
                }
            }
            """);
        using var session = new GraphSession(_fs, canonicalIdVersion: CanonicalSymbolIdVersion.AssemblyQualifiedV2);
        session.Load(_root, graphPath: null, rulesPath: null, readOnly: false, defineProfiles: new[] { "Editor", "Player" });
        var snapshot = session.CurrentSnapshot;
        var provider = session.OperationFactProvider;
        var root = session.Graph!.Symbols.Single(symbol => symbol.Name == "Run").Id;
        var handler = new MethodEffectToolHandler(session);
        var report = handler.Execute(Args(new { symbolIds = new[] { root }, profileScope = "Player" }), default);
        Assert.Equal(OperationFactExecutionMode.EphemeralProfileCompilation, report.ScanReceipt.ExecutionMode);
        Assert.Equal(0, report.ScanReceipt.AdditionalSemanticBaseCount);
        Assert.Equal(1, report.ScanReceipt.CompiledModuleCount);
        Assert.Equal(1, Assert.Single(report.Methods).CallCount);
        Assert.Contains(Assert.Single(report.Methods).MemberAccesses, access => access.Kind == OperationFactKind.MemberWrite);
        Assert.Same(snapshot, session.CurrentSnapshot);
        Assert.Same(provider, session.OperationFactProvider);
        File.AppendAllText(sourcePath, "\n// changed after the retained publication\n");
        var rejected = handler.Execute(Args(new { symbolIds = new[] { root }, profileScope = "Player" }), default);
        Assert.Equal(OperationFactScanStatus.Rejected, rejected.ScanReceipt.Status);
        Assert.Equal(0, rejected.ScanReceipt.EmittedFactCount);
        Assert.Contains(Assert.Single(rejected.Methods).UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.ScanRejected);
        Assert.Same(snapshot, session.CurrentSnapshot);
    }

    private sealed class BlastRadius : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
