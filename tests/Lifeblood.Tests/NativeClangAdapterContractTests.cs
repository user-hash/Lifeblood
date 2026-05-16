using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Contract tests for the external Native Clang adapter graph shape. The
/// fixture graphs pin the language-agnostic output that the libclang pipeline
/// must emit while keeping LLVM outside Lifeblood core.
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

    [Fact]
    public void DirectRefsFixture_ModelsEnumsTypedefsGlobalsAndVariableReferences()
    {
        var graph = LoadDirectRefsFixture().Graph;

        var validationErrors = GraphValidator.Validate(graph);
        Assert.Empty(validationErrors);

        AssertSymbol(graph, "type:PacketKind", SymbolKind.Type, "enum", "direct-refs-debug");
        AssertSymbol(graph, "field:PacketKind.PacketKind_Video", SymbolKind.Field, "enumMember", "direct-refs-debug");
        AssertSymbol(graph, "type:PacketKindAlias", SymbolKind.Type, "typedef", "direct-refs-debug");
        AssertSymbol(graph, "field:Packet.kind", SymbolKind.Field, "structField", "direct-refs-debug");
        AssertSymbol(graph, "field:decode_bias", SymbolKind.Field, "global", "direct-refs-debug");

        var enumMember = graph.GetSymbol("field:PacketKind.PacketKind_Video");
        Assert.NotNull(enumMember);
        Assert.Equal("2", enumMember!.Properties["native.enumValue"]);

        var typedef = graph.GetSymbol("type:PacketKindAlias");
        Assert.NotNull(typedef);
        Assert.Equal("PacketKind", typedef!.Properties["native.underlyingType"]);

        var global = graph.GetSymbol("field:decode_bias");
        Assert.NotNull(global);
        Assert.Equal("external", global!.Properties["native.linkage"]);
        Assert.Equal("int", global.Properties["native.fieldType"]);

        AssertReferenceKind(graph, "type:PacketKindAlias", "type:PacketKind", "underlyingType");
        AssertReferenceKind(graph, "field:Packet.kind", "type:PacketKindAlias", "fieldType");
        AssertReferenceKind(graph, "method:decode(Packet*)", "field:decode_bias", "globalAccess");
        AssertReferenceKind(graph, "method:decode(Packet*)", "field:PacketKind.PacketKind_Video", "enumMember");
        AssertReferenceKind(graph, "method:decode(Packet*)", "field:Packet.kind", "fieldAccess");
    }

    [Fact]
    public void DirectRefsFixture_BlastRadiusReusesGlobalReferences()
    {
        var graph = LoadDirectRefsFixture().Graph;

        var result = BlastRadiusAnalyzer.Analyze(graph, "field:decode_bias");

        Assert.Contains("method:decode(Packet*)", result.AffectedSymbolIds);
    }

    [Fact]
    public void ProfileFixtures_SurfaceCommandLineDefinesAndMacroSymbols()
    {
        var video = LoadProfileFixture("video").Graph;
        var audio = LoadProfileFixture("audio").Graph;

        Assert.Empty(GraphValidator.Validate(video));
        Assert.Empty(GraphValidator.Validate(audio));

        AssertModuleProfile(video, "video", "ENABLE_VIDEO=1;PROFILE_NAME=video");
        AssertModuleProfile(audio, "audio", "ENABLE_AUDIO=1;PROFILE_NAME=audio");

        AssertSymbol(video, "field:macro:ENABLE_VIDEO", SymbolKind.Field, "macro", "video");
        AssertSymbol(video, "field:macro:PACKET_BASE", SymbolKind.Field, "macro", "video");
        AssertSymbol(video, "field:macro:PROFILE_KIND", SymbolKind.Field, "macro", "video");
        AssertSymbol(audio, "field:macro:ENABLE_AUDIO", SymbolKind.Field, "macro", "audio");
        AssertSymbol(audio, "field:macro:PACKET_BASE", SymbolKind.Field, "macro", "audio");
        AssertSymbol(audio, "field:macro:PROFILE_KIND", SymbolKind.Field, "macro", "audio");

        Assert.Equal("commandLine", video.GetSymbol("field:macro:ENABLE_VIDEO")!.Properties["native.macroSource"]);
        Assert.Equal("source", video.GetSymbol("field:macro:PACKET_BASE")!.Properties["native.macroSource"]);
        Assert.Equal("commandLine", audio.GetSymbol("field:macro:ENABLE_AUDIO")!.Properties["native.macroSource"]);
        Assert.Equal("source", audio.GetSymbol("field:macro:PACKET_BASE")!.Properties["native.macroSource"]);

        AssertReferenceKind(video, "file:src/codec.c", "field:macro:PROFILE_KIND", "macroExpansion", EvidenceKind.Syntax);
        AssertReferenceKind(video, "file:src/codec.c", "field:macro:PACKET_BASE", "macroExpansion", EvidenceKind.Syntax);
        AssertReferenceKind(audio, "file:src/codec.c", "field:macro:PROFILE_KIND", "macroExpansion", EvidenceKind.Syntax);
        AssertReferenceKind(audio, "file:src/codec.c", "field:macro:PACKET_BASE", "macroExpansion", EvidenceKind.Syntax);
    }

    [Fact]
    public void ProfileFixtures_DifferentDefinesProduceDifferentReachableFunctions()
    {
        var video = LoadProfileFixture("video").Graph;
        var audio = LoadProfileFixture("audio").Graph;

        Assert.NotNull(video.GetSymbol("method:decode_video(Packet*)"));
        Assert.NotNull(video.GetSymbol("method:scale_video(int)"));
        Assert.Null(video.GetSymbol("method:decode_audio(Packet*)"));
        Assert.Null(video.GetSymbol("method:scale_audio(int)"));

        Assert.NotNull(audio.GetSymbol("method:decode_audio(Packet*)"));
        Assert.NotNull(audio.GetSymbol("method:scale_audio(int)"));
        Assert.Null(audio.GetSymbol("method:decode_video(Packet*)"));
        Assert.Null(audio.GetSymbol("method:scale_video(int)"));

        AssertEdge(video, "method:decode_video(Packet*)", "method:scale_video(int)", EdgeKind.Calls);
        AssertEdge(audio, "method:decode_audio(Packet*)", "method:scale_audio(int)", EdgeKind.Calls);
    }

    [Fact]
    public void CallbackTableFixture_ModelsTableHeldFunctionReferences()
    {
        var graph = LoadCallbackFixture().Graph;

        Assert.Empty(GraphValidator.Validate(graph));

        AssertSymbol(graph, "field:codec_table", SymbolKind.Field, "callbackTable", "callback-debug");
        AssertSymbol(graph, "field:codec_table:row:0", SymbolKind.Field, "tableRow", "callback-debug");
        AssertSymbol(graph, "field:codec_table:row:0:cell:0", SymbolKind.Field, "tableCell", "callback-debug");
        AssertSymbol(graph, "field:codec_table:row:1", SymbolKind.Field, "tableRow", "callback-debug");
        AssertSymbol(graph, "field:codec_table:row:1:cell:0", SymbolKind.Field, "tableCell", "callback-debug");

        var table = graph.GetSymbol("field:codec_table");
        Assert.NotNull(table);
        Assert.Equal("true", table!.Properties["native.callbackTable"]);
        Assert.Equal("2", table.Properties["native.tableRowCount"]);

        AssertCallbackTableCell(
            graph,
            "field:codec_table",
            rowOrdinal: 0,
            cellOrdinal: 0,
            methodId: "method:decode_audio(Packet*)");
        AssertCallbackTableCell(
            graph,
            "field:codec_table",
            rowOrdinal: 1,
            cellOrdinal: 0,
            methodId: "method:decode_video(Packet*)");

        AssertReferenceKind(
            graph,
            "field:codec_table",
            "method:decode_audio(Packet*)",
            "callbackTarget");
        AssertReferenceKind(
            graph,
            "field:codec_table",
            "method:decode_video(Packet*)",
            "callbackTarget");
        AssertReferenceKind(
            graph,
            "method:dispatch_first(Packet*)",
            "field:codec_table",
            "globalAccess");
    }

    [Fact]
    public void CallbackTableFixture_BlastRadiusSeesRegisteredCallbacksAsLive()
    {
        var graph = LoadCallbackFixture().Graph;

        var result = BlastRadiusAnalyzer.Analyze(graph, "method:decode_audio(Packet*)");

        Assert.Contains("field:codec_table", result.AffectedSymbolIds);
        Assert.Contains("method:dispatch_first(Packet*)", result.AffectedSymbolIds);
    }

    private static void AssertSymbol(
        SemanticGraph graph,
        string id,
        SymbolKind kind,
        string nativeKind,
        string buildProfile = "tiny-debug")
    {
        var symbol = graph.GetSymbol(id);
        Assert.NotNull(symbol);
        Assert.Equal(kind, symbol!.Kind);
        Assert.Equal(nativeKind, symbol.Properties["native.kind"]);
        Assert.Equal(buildProfile, symbol.Properties["native.buildProfile"]);
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

    private static void AssertReferenceKind(
        SemanticGraph graph,
        string sourceId,
        string targetId,
        string referenceKind,
        EvidenceKind evidenceKind = EvidenceKind.Semantic)
    {
        var edge = AssertEdge(graph, sourceId, targetId, EdgeKind.References);
        Assert.Equal(evidenceKind, edge.Evidence.Kind);
        Assert.Equal(ConfidenceLevel.Proven, edge.Evidence.Confidence);
        Assert.Equal(referenceKind, edge.Properties["native.referenceKind"]);
    }

    private static void AssertCallbackTableCell(
        SemanticGraph graph,
        string tableId,
        int rowOrdinal,
        int cellOrdinal,
        string methodId)
    {
        var rowId = $"{tableId}:row:{rowOrdinal}";
        var row = graph.GetSymbol(rowId);
        Assert.NotNull(row);
        Assert.Equal(tableId, row!.ParentId);
        Assert.Equal(tableId, row.Properties["native.tableOwnerId"]);
        Assert.Equal(rowOrdinal.ToString(), row.Properties["native.tableRowOrdinal"]);
        Assert.Equal("1", row.Properties["native.tableCellCount"]);

        var cell = graph.GetSymbol($"{rowId}:cell:{cellOrdinal}");
        Assert.NotNull(cell);
        Assert.Equal(rowId, cell!.ParentId);
        Assert.Equal(tableId, cell.Properties["native.tableOwnerId"]);
        Assert.Equal(rowOrdinal.ToString(), cell.Properties["native.tableRowOrdinal"]);
        Assert.Equal(cellOrdinal.ToString(), cell.Properties["native.tableCellOrdinal"]);
        Assert.Equal("MethodGroup", cell.Properties["native.tableValueKind"]);
        Assert.Equal(methodId, cell.Properties["native.methodGroupId"]);
        Assert.Equal(methodId, cell.Properties["native.callbackTargetId"]);
    }

    private static void AssertModuleProfile(
        SemanticGraph graph,
        string profile,
        string defines)
    {
        var module = graph.GetSymbol("mod:profile-c");
        Assert.NotNull(module);
        Assert.Equal(profile, module!.Properties["native.buildProfile"]);
        Assert.Equal("1", module.Properties["native.translationUnitCount"]);
        Assert.Equal(defines, module.Properties["native.defines"]);
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
        => LoadFixture("tiny-c");

    private static GraphDocument LoadDirectRefsFixture()
        => LoadFixture("direct-refs-c");

    private static GraphDocument LoadProfileFixture(string profile)
        => LoadFixture("profile-c", $"expected.{profile}.graph.json");

    private static GraphDocument LoadCallbackFixture()
        => LoadFixture("callback-table-c");

    private static GraphDocument LoadFixture(string fixtureName)
        => LoadFixture(fixtureName, "expected.graph.json");

    private static GraphDocument LoadFixture(string fixtureName, string graphFileName)
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "adapters",
            "native-clang",
            "test-fixtures",
            fixtureName,
            graphFileName);

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
