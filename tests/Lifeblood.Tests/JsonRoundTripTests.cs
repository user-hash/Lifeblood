using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class JsonRoundTripTests
{
    [Fact]
    public void RoundTrip_SymbolsPreserved()
    {
        var original = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Foo", Name = "Foo", QualifiedName = "App.Foo",
                Kind = SymbolKind.Type, FilePath = "Foo.cs", Line = 10,
                Visibility = Visibility.Public, IsAbstract = true,
                Properties = new Dictionary<string, string> { ["typeKind"] = "class" },
            })
            .Build();

        var roundTripped = RoundTrip(original);

        Assert.Equal(original.Symbols.Length, roundTripped.Symbols.Length);
        var sym = roundTripped.GetSymbol("type:Foo");
        Assert.NotNull(sym);
        Assert.Equal("Foo", sym!.Name);
        Assert.Equal("App.Foo", sym.QualifiedName);
        Assert.Equal(SymbolKind.Type, sym.Kind);
        Assert.Equal("Foo.cs", sym.FilePath);
        Assert.Equal(10, sym.Line);
        Assert.Equal(Visibility.Public, sym.Visibility);
        Assert.True(sym.IsAbstract);
        Assert.Equal("class", sym.Properties["typeKind"]);
    }

    [Fact]
    public void RoundTrip_EdgesPreserved()
    {
        var original = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge
            {
                SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Implements,
                Evidence = new Evidence
                {
                    Kind = EvidenceKind.Semantic,
                    AdapterName = "Roslyn",
                    SourceSpan = "A.cs:5",
                    Confidence = ConfidenceLevel.Proven,
                },
            })
            .Build();

        var roundTripped = RoundTrip(original);

        // Find the Implements edge (there may also be Contains edges from ParentId)
        var implEdge = roundTripped.Edges.FirstOrDefault(e => e.Kind == EdgeKind.Implements);
        Assert.NotNull(implEdge);
        Assert.Equal("type:A", implEdge!.SourceId);
        Assert.Equal("type:B", implEdge.TargetId);
        Assert.Equal(EvidenceKind.Semantic, implEdge.Evidence.Kind);
        Assert.Equal("Roslyn", implEdge.Evidence.AdapterName);
        Assert.Equal(ConfidenceLevel.Proven, implEdge.Evidence.Confidence);
    }

    [Fact]
    public void RoundTrip_ContainmentSynthesized()
    {
        var original = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .Build();

        var roundTripped = RoundTrip(original);

        var containsEdges = roundTripped.Edges.Where(e => e.Kind == EdgeKind.Contains).ToArray();
        Assert.Single(containsEdges);
    }

    private static SemanticGraph RoundTrip(SemanticGraph graph)
    {
        var exporter = new JsonGraphExporter();
        var importer = new JsonGraphImporter();

        using var ms = new MemoryStream();
        exporter.Export(graph, ms);
        ms.Position = 0;
        return importer.Import(ms);
    }
}
