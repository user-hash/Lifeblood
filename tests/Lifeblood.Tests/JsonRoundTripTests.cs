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
        var doc = MakeDocument(new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Foo", Name = "Foo", QualifiedName = "App.Foo",
                Kind = SymbolKind.Type, FilePath = "Foo.cs", Line = 10,
                Visibility = Visibility.Public, IsAbstract = true,
                Properties = new Dictionary<string, string> { ["typeKind"] = "class" },
            })
            .Build());

        var roundTripped = RoundTrip(doc);

        Assert.Equal(doc.Graph.Symbols.Count, roundTripped.Graph.Symbols.Count);
        var sym = roundTripped.Graph.GetSymbol("type:Foo");
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
    public void RoundTrip_DefaultEnumValues_NotDropped()
    {
        // Regression: WhenWritingDefault used to drop Module (index 0) and Contains (index 0)
        var doc = MakeDocument(new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .Build());

        // Export to JSON and inspect raw bytes
        using var ms = new MemoryStream();
        new JsonGraphExporter().Export(doc, ms);
        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Contains("\"module\"", json);   // SymbolKind.Module must be present
        Assert.Contains("\"contains\"", json);  // EdgeKind.Contains must be present
    }

    [Fact]
    public void RoundTrip_EdgesPreserved()
    {
        var doc = MakeDocument(new GraphBuilder()
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
            .Build());

        var roundTripped = RoundTrip(doc);

        var implEdge = roundTripped.Graph.Edges.FirstOrDefault(e => e.Kind == EdgeKind.Implements);
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
        var doc = MakeDocument(new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "mod:Core" })
            .Build());

        var roundTripped = RoundTrip(doc);

        var containsEdges = roundTripped.Graph.Edges.Where(e => e.Kind == EdgeKind.Contains).ToArray();
        Assert.Single(containsEdges);
    }

    [Fact]
    public void RoundTrip_AdapterMetadataPreserved()
    {
        var doc = new GraphDocument
        {
            Version = "1.0",
            Language = "csharp",
            Adapter = new AdapterCapability
            {
                Language = "csharp",
                AdapterName = "Roslyn",
                AdapterVersion = "1.0.0",
                CanDiscoverSymbols = true,
                TypeResolution = ConfidenceLevel.Proven,
                CallResolution = ConfidenceLevel.Proven,
                CrossModuleReferences = ConfidenceLevel.BestEffort,
                OverrideResolution = ConfidenceLevel.None,
            },
            Graph = new GraphBuilder()
                .AddSymbol(new Symbol { Id = "mod:App", Name = "App", Kind = SymbolKind.Module })
                .Build(),
        };

        var roundTripped = RoundTrip(doc);

        Assert.Equal("1.0", roundTripped.Version);
        Assert.Equal("csharp", roundTripped.Language);
        Assert.NotNull(roundTripped.Adapter);
        Assert.Equal("Roslyn", roundTripped.Adapter!.AdapterName);
        Assert.Equal("1.0.0", roundTripped.Adapter.AdapterVersion);
        Assert.True(roundTripped.Adapter.CanDiscoverSymbols);
        Assert.Equal(ConfidenceLevel.Proven, roundTripped.Adapter.TypeResolution);
        Assert.Equal(ConfidenceLevel.BestEffort, roundTripped.Adapter.CrossModuleReferences);
        Assert.Equal(ConfidenceLevel.None, roundTripped.Adapter.OverrideResolution);
    }

    [Fact]
    public void RoundTrip_PropertyKind_Preserved()
    {
        var doc = MakeDocument(new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Foo", Name = "Foo", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "property:Foo.Name", Name = "Name", Kind = SymbolKind.Property,
                ParentId = "type:Foo", FilePath = "Foo.cs", Line = 5,
            })
            .Build());

        var roundTripped = RoundTrip(doc);

        var prop = roundTripped.Graph.GetSymbol("property:Foo.Name");
        Assert.NotNull(prop);
        Assert.Equal(SymbolKind.Property, prop!.Kind);
        Assert.Equal("Foo.cs", prop.FilePath);
    }

    private static GraphDocument MakeDocument(SemanticGraph graph)
        => new() { Graph = graph };

    private static GraphDocument RoundTrip(GraphDocument doc)
    {
        var exporter = new JsonGraphExporter();
        var importer = new JsonGraphImporter();

        using var ms = new MemoryStream();
        exporter.Export(doc, ms);
        ms.Position = 0;
        return importer.ImportDocument(ms);
    }

    // INV-JSON-IMPORT-BOM-001: importer must accept any of the standard
    // BOM-flagged encodings, not just bare UTF-8. The release-blocker
    // dogfood case: `lifeblood export --project ... > graph.json` on
    // Windows PowerShell writes UTF-16LE-with-BOM by default; passing the
    // resulting file straight to `lifeblood analyze --graph graph.json`
    // crashed pre-fix with an unhandled JSON exception because
    // System.Text.Json's stream-deserialize path requires UTF-8.
    [Theory]
    [InlineData("utf-8-no-bom")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16-le-bom")]
    [InlineData("utf-16-be-bom")]
    public void ImportDocument_AcceptsAllStandardBomEncodings(string encodingName)
    {
        // Build a real graph, serialize once (UTF-8), then transcode the
        // bytes to the target encoding so the import path sees exactly the
        // bytes a `> graph.json` redirect would have produced.
        var doc = MakeDocument(new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:NS.Foo", Name = "Foo", QualifiedName = "NS.Foo",
                Kind = SymbolKind.Type, FilePath = "Foo.cs",
            })
            .Build());

        string utf8Json;
        using (var ms = new MemoryStream())
        {
            new JsonGraphExporter().Export(doc, ms);
            utf8Json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        var encoding = encodingName switch
        {
            "utf-8-no-bom"  => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-8-bom"     => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-16-le-bom" => (System.Text.Encoding)new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16-be-bom" => new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName)),
        };

        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(utf8Json)).ToArray();
        using var input = new MemoryStream(bytes);

        var roundTripped = new JsonGraphImporter().ImportDocument(input);

        var sym = roundTripped.Graph.GetSymbol("type:NS.Foo");
        Assert.NotNull(sym);
        Assert.Equal("Foo", sym!.Name);
        Assert.Equal("NS.Foo", sym.QualifiedName);
    }
}
