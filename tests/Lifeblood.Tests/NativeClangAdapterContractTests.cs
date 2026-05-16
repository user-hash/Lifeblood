using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Stage-1 contract tests for the planned external Native Clang adapter.
/// The fixture graph is hand-authored until extractor code exists; it pins
/// the language-agnostic graph shape that the future Clang pipeline must emit.
/// </summary>
public class NativeClangAdapterContractTests
{
    [Fact]
    public void TinyFixture_ImportsWithHonestAdapterMetadata()
    {
        var doc = LoadTinyFixture();

        Assert.Equal("1.0", doc.Version);
        Assert.Equal("c", doc.Language);
        Assert.NotNull(doc.Adapter);
        Assert.Equal("native-clang-fixture", doc.Adapter!.AdapterName);
        Assert.Equal("0.0.0-fixture", doc.Adapter.AdapterVersion);
        Assert.True(doc.Adapter.CanDiscoverSymbols);
        Assert.Equal(ConfidenceLevel.Proven, doc.Adapter.TypeResolution);
        Assert.Equal(ConfidenceLevel.Proven, doc.Adapter.CallResolution);
        Assert.Equal(ConfidenceLevel.None, doc.Adapter.ImplementationResolution);
        Assert.Equal(ConfidenceLevel.None, doc.Adapter.CrossModuleReferences);
        Assert.Equal(ConfidenceLevel.None, doc.Adapter.OverrideResolution);
    }

    [Fact]
    public void TinyFixture_ValidatesAndRunsExistingAnalysis()
    {
        var graph = LoadTinyFixture().Graph;

        var validationErrors = GraphValidator.Validate(graph);
        Assert.Empty(validationErrors);

        var analysis = AnalysisPipeline.Run(graph);
        Assert.Equal(1, analysis.Metrics.TotalModules);
        Assert.Equal(1, analysis.Metrics.TotalTypes);
        Assert.Equal(0, analysis.Metrics.ViolationCount);
        Assert.Empty(analysis.Cycles);
    }

    [Fact]
    public void TinyFixture_UsesLanguageAgnosticSymbolKindsWithNativeProperties()
    {
        var graph = LoadTinyFixture().Graph;

        AssertSymbol(graph, "mod:tiny-c", SymbolKind.Module, "library");
        AssertSymbol(graph, "file:src/decode.c", SymbolKind.File, "translationUnit");
        AssertSymbol(graph, "file:src/packet.h", SymbolKind.File, "header");
        AssertSymbol(graph, "type:Packet", SymbolKind.Type, "struct");
        AssertSymbol(graph, "field:Packet.size", SymbolKind.Field, "structField");
        AssertSymbol(graph, "method:clamp(int)", SymbolKind.Method, "function");
        AssertSymbol(graph, "method:decode(Packet*)", SymbolKind.Method, "function");

        var clamp = graph.GetSymbol("method:clamp(int)");
        Assert.NotNull(clamp);
        Assert.Equal(Visibility.Private, clamp!.Visibility);
        Assert.True(clamp.IsStatic);
        Assert.Equal("internal", clamp.Properties["native.linkage"]);

        var decode = graph.GetSymbol("method:decode(Packet*)");
        Assert.NotNull(decode);
        Assert.Equal(Visibility.Public, decode!.Visibility);
        Assert.Equal("external", decode.Properties["native.linkage"]);
        Assert.Equal("int (struct Packet *)", decode.Properties["native.signature"]);
    }

    [Fact]
    public void TinyFixture_ParentIdsSynthesizeContainsEdges()
    {
        var graph = LoadTinyFixture().Graph;

        AssertContains(graph, "mod:tiny-c", "file:src/decode.c");
        AssertContains(graph, "mod:tiny-c", "file:src/packet.h");
        AssertContains(graph, "file:src/packet.h", "type:Packet");
        AssertContains(graph, "type:Packet", "field:Packet.size");
        AssertContains(graph, "file:src/decode.c", "method:clamp(int)");
        AssertContains(graph, "file:src/decode.c", "method:decode(Packet*)");
    }

    [Fact]
    public void TinyFixture_DirectCallAndReferencesHaveEvidenceAndCallSites()
    {
        var graph = LoadTinyFixture().Graph;

        var call = AssertEdge(
            graph,
            sourceId: "method:decode(Packet*)",
            targetId: "method:clamp(int)",
            kind: EdgeKind.Calls);
        Assert.Equal(EvidenceKind.Semantic, call.Evidence.Kind);
        Assert.Equal(ConfidenceLevel.Proven, call.Evidence.Confidence);
        Assert.Equal("direct", call.Properties["native.callKind"]);
        AssertCallSite(call, "src/decode.c", line: 10, column: 12, containingSymbolId: "method:decode(Packet*)");

        var parameterType = AssertEdge(
            graph,
            sourceId: "method:decode(Packet*)",
            targetId: "type:Packet",
            kind: EdgeKind.References);
        Assert.Equal("parameterType", parameterType.Properties["native.referenceKind"]);
        AssertCallSite(parameterType, "src/decode.c", line: 8, column: 19, containingSymbolId: "method:decode(Packet*)");

        var fieldAccess = AssertEdge(
            graph,
            sourceId: "method:decode(Packet*)",
            targetId: "field:Packet.size",
            kind: EdgeKind.References);
        Assert.Equal("fieldAccess", fieldAccess.Properties["native.referenceKind"]);
        AssertCallSite(fieldAccess, "src/decode.c", line: 10, column: 26, containingSymbolId: "method:decode(Packet*)");

        var include = AssertEdge(
            graph,
            sourceId: "file:src/decode.c",
            targetId: "file:src/packet.h",
            kind: EdgeKind.References);
        Assert.Equal(EvidenceKind.Syntax, include.Evidence.Kind);
        Assert.Equal("include", include.Properties["native.kind"]);
        Assert.Equal("packet.h", include.Properties["native.include"]);
        AssertCallSite(include, "src/decode.c", line: 1, column: 1, containingSymbolId: "file:src/decode.c");
    }

    [Fact]
    public void TinyFixture_BlastRadiusReusesExistingAnalysis()
    {
        var graph = LoadTinyFixture().Graph;

        var result = BlastRadiusAnalyzer.Analyze(graph, "method:clamp(int)");

        Assert.Equal("method:clamp(int)", result.TargetSymbolId);
        Assert.Contains("method:decode(Packet*)", result.AffectedSymbolIds);
        Assert.Contains(result.Breaks, b =>
            b.SymbolId == "method:decode(Packet*)" &&
            b.Kind == Lifeblood.Domain.Results.BreakKind.BindingRemoval);
    }

    private static void AssertSymbol(
        SemanticGraph graph,
        string id,
        SymbolKind kind,
        string nativeKind)
    {
        var symbol = graph.GetSymbol(id);
        Assert.NotNull(symbol);
        Assert.Equal(kind, symbol!.Kind);
        Assert.Equal(nativeKind, symbol.Properties["native.kind"]);
        Assert.Equal("tiny-debug", symbol.Properties["native.buildProfile"]);
    }

    private static void AssertContains(SemanticGraph graph, string sourceId, string targetId)
        => AssertEdge(graph, sourceId, targetId, EdgeKind.Contains);

    private static Edge AssertEdge(
        SemanticGraph graph,
        string sourceId,
        string targetId,
        EdgeKind kind)
    {
        var edge = graph.Edges.FirstOrDefault(e =>
            e.SourceId == sourceId &&
            e.TargetId == targetId &&
            e.Kind == kind);
        Assert.NotNull(edge);
        return edge!;
    }

    private static void AssertCallSite(
        Edge edge,
        string filePath,
        int line,
        int column,
        string containingSymbolId)
    {
        Assert.NotNull(edge.CallSite);
        Assert.Equal(filePath, edge.CallSite!.FilePath);
        Assert.Equal(line, edge.CallSite.Line);
        Assert.Equal(column, edge.CallSite.Column);
        Assert.Equal(containingSymbolId, edge.CallSite.ContainingSymbolId);
    }

    private static GraphDocument LoadTinyFixture()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "adapters",
            "native-clang",
            "test-fixtures",
            "tiny-c",
            "expected.graph.json");

        using var stream = File.OpenRead(path);
        return new JsonGraphImporter().ImportDocument(stream);
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
}
