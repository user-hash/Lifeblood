using System.Diagnostics;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchets the native executable itself against source fixtures. These tests
/// skip when the C++ adapter has not been built, but when present they prove
/// the current extractor emits graph facts useful for mapping and improvement
/// workflows.
/// </summary>
public class NativeClangExecutableRatchetTests
{
    [SkippableFact]
    public void Executable_TinyFixture_EmitsValidMappingGraph()
    {
        var graph = RunFixture("tiny-c", "tiny-debug").Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        Assert.NotNull(graph.GetSymbol("mod:tiny-c"));
        Assert.NotNull(graph.GetSymbol("file:src/decode.c"));
        Assert.NotNull(graph.GetSymbol("file:src/packet.h"));
        Assert.NotNull(graph.GetSymbol("type:Packet"));
        Assert.NotNull(graph.GetSymbol("field:Packet.size"));
        Assert.NotNull(graph.GetSymbol("method:decode(Packet*)"));
        Assert.NotNull(graph.GetSymbol("method:clamp(int)"));

        AssertModuleParseHealth(graph, "mod:tiny-c", total: 1, parsed: 1, failed: 0);
        AssertModuleFileInventory(graph, "mod:tiny-c", translationUnits: 1, headers: 1);
        AssertModuleGraphInventory(graph, "mod:tiny-c", symbols: 7, edges: 4, references: 3, calls: 1);
        AssertModuleFunctionInventory(graph, "mod:tiny-c", definitions: 2, declarations: 0);
        AssertModuleNativeKindInventory(graph, "mod:tiny-c", macros: 1);
        AssertModuleVisibilityInventory(graph, "mod:tiny-c", publicSymbols: 3, privateSymbols: 1, internalSymbols: 3);
        AssertModuleBuildFacts(
            graph,
            "mod:tiny-c",
            includePaths: 1,
            systemIncludePaths: 0,
            quoteIncludePaths: 0,
            sourceLanguages: "c",
            languageStandards: "c11");
        AssertTranslationUnitBuildInputs(
            graph,
            "file:src/decode.c",
            defines: 0,
            undefines: 0,
            includePaths: 1,
            languageStandard: "c11");
        AssertFilePressure(
            graph,
            "file:src/decode.c",
            declaredSymbols: 2,
            publicDeclaredSymbols: 1,
            privateDeclaredSymbols: 1,
            internalDeclaredSymbols: 0,
            functionDefinitions: 2,
            functionDeclarations: 0,
            macros: 0,
            outgoingReferences: 3,
            incomingReferences: 0,
            outgoingCalls: 1,
            incomingCalls: 1);
        AssertFilePressure(
            graph,
            "file:src/packet.h",
            declaredSymbols: 3,
            publicDeclaredSymbols: 2,
            privateDeclaredSymbols: 0,
            internalDeclaredSymbols: 1,
            functionDefinitions: 0,
            functionDeclarations: 0,
            macros: 1,
            outgoingReferences: 0,
            incomingReferences: 3,
            outgoingCalls: 0,
            incomingCalls: 0);
        AssertEdge(graph, "file:src/decode.c", "file:src/packet.h", EdgeKind.References);
        AssertReferenceKind(graph, "method:decode(Packet*)", "type:Packet", "parameterType");
        AssertReferenceKind(graph, "method:decode(Packet*)", "field:Packet.size", "fieldAccess");
        AssertCall(graph, "method:decode(Packet*)", "method:clamp(int)");
        AssertAllNativeFactsCarryBuildProfile(graph, "tiny-debug");
    }

    [SkippableFact]
    public void Executable_ProfileFixtures_KeepBuildConfigGraphsSeparate()
    {
        var video = RunFixture(
            "profile-c",
            "video",
            compilationDatabase: Path.Combine("profiles", "video")).Graph;
        var audio = RunFixture(
            "profile-c",
            "audio",
            compilationDatabase: Path.Combine("profiles", "audio")).Graph;

        Assert.Empty(GraphValidator.Validate(video));
        Assert.Empty(GraphValidator.Validate(audio));

        Assert.NotNull(video.GetSymbol("method:decode_video(Packet*)"));
        Assert.NotNull(video.GetSymbol("method:scale_video(int)"));
        Assert.Null(video.GetSymbol("method:decode_audio(Packet*)"));
        Assert.Null(video.GetSymbol("method:scale_audio(int)"));

        Assert.NotNull(audio.GetSymbol("method:decode_audio(Packet*)"));
        Assert.NotNull(audio.GetSymbol("method:scale_audio(int)"));
        Assert.Null(audio.GetSymbol("method:decode_video(Packet*)"));
        Assert.Null(audio.GetSymbol("method:scale_video(int)"));

        AssertModuleParseHealth(video, "mod:profile-c", total: 1, parsed: 1, failed: 0);
        AssertModuleParseHealth(audio, "mod:profile-c", total: 1, parsed: 1, failed: 0);
        AssertModuleFileInventory(video, "mod:profile-c", translationUnits: 1, headers: 1);
        AssertModuleFileInventory(audio, "mod:profile-c", translationUnits: 1, headers: 1);
        AssertModuleBuildFacts(
            video,
            "mod:profile-c",
            includePaths: 1,
            systemIncludePaths: 1,
            quoteIncludePaths: 1,
            sourceLanguages: "c");
        AssertModuleBuildFacts(
            audio,
            "mod:profile-c",
            includePaths: 1,
            systemIncludePaths: 0,
            quoteIncludePaths: 0,
            sourceLanguages: "c");
        AssertTranslationUnitBuildInputs(
            video,
            "file:src/codec.c",
            defines: 2,
            undefines: 0,
            includePaths: 1,
            systemIncludePaths: 1,
            quoteIncludePaths: 1);
        AssertTranslationUnitBuildInputs(audio, "file:src/codec.c", defines: 2, undefines: 1);
        AssertModuleUndefines(audio, "mod:profile-c", "LEGACY_CODEC");
        AssertCall(video, "method:decode_video(Packet*)", "method:scale_video(int)");
        AssertCall(audio, "method:decode_audio(Packet*)", "method:scale_audio(int)");
        AssertAllNativeFactsCarryBuildProfile(video, "video");
        AssertAllNativeFactsCarryBuildProfile(audio, "audio");
    }

    [SkippableFact]
    public void Executable_CallbackFixture_EmitsImprovementRelevantDispatchFacts()
    {
        var graph = RunFixture("callback-table-c", "callback-debug").Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        var table = graph.GetSymbol("field:codec_table");
        Assert.NotNull(table);
        Assert.Equal("callbackTable", table!.Properties["native.kind"]);
        Assert.Equal("true", table.Properties["native.callbackTable"]);

        AssertModuleParseHealth(graph, "mod:callback-table-c", total: 1, parsed: 1, failed: 0);
        AssertModuleGraphInventory(
            graph,
            "mod:callback-table-c",
            symbols: 13,
            edges: 11,
            references: 11,
            calls: 0);
        AssertModuleNativeKindInventory(graph, "mod:callback-table-c", macros: 1, callbackTables: 1);
        AssertFileNativeKindInventory(graph, "file:src/registry.c", macros: 0, callbackTables: 1);
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
        AssertReferenceCounts(graph, "field:codec_table", incoming: 1, outgoing: 2);
        AssertReferenceCounts(graph, "method:dispatch_first(Packet*)", incoming: 0, outgoing: 3);
        AssertReferenceCounts(graph, "method:decode_audio(Packet*)", incoming: 1, outgoing: 2);

        var blast = BlastRadiusAnalyzer.Analyze(graph, "method:decode_audio(Packet*)");
        Assert.Contains("field:codec_table", blast.AffectedSymbolIds);
        Assert.Contains("method:dispatch_first(Packet*)", blast.AffectedSymbolIds);
        AssertAllNativeFactsCarryBuildProfile(graph, "callback-debug");
    }

    [SkippableFact]
    public void Executable_MultiTranslationUnitFixture_ReportsWholeBuildHealth()
    {
        var graph = RunFixture("multi-tu-c", "multi-debug").Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        AssertModuleParseHealth(graph, "mod:multi-tu-c", total: 2, parsed: 2, failed: 0);
        AssertModuleFileInventory(graph, "mod:multi-tu-c", translationUnits: 2, headers: 1);
        AssertModuleGraphInventory(graph, "mod:multi-tu-c", symbols: 10, edges: 8, references: 6, calls: 2);
        AssertModuleFunctionInventory(graph, "mod:multi-tu-c", definitions: 4, declarations: 0);
        AssertModuleNativeKindInventory(graph, "mod:multi-tu-c", macros: 1);
        AssertTranslationUnitHealth(graph, "file:src/audio.c", "parsed");
        AssertTranslationUnitHealth(graph, "file:src/video.c", "parsed");
        AssertTranslationUnitBuildInputs(graph, "file:src/audio.c", defines: 0, undefines: 0);
        AssertTranslationUnitBuildInputs(graph, "file:src/video.c", defines: 0, undefines: 0);

        Assert.NotNull(graph.GetSymbol("file:src/audio.c"));
        Assert.NotNull(graph.GetSymbol("file:src/video.c"));
        Assert.NotNull(graph.GetSymbol("file:src/packet.h"));
        Assert.NotNull(graph.GetSymbol("method:decode_audio(Packet*)"));
        Assert.NotNull(graph.GetSymbol("method:audio_gain(int)"));
        Assert.NotNull(graph.GetSymbol("method:decode_video(Packet*)"));
        Assert.NotNull(graph.GetSymbol("method:video_scale(int)"));

        AssertEdge(graph, "file:src/audio.c", "file:src/packet.h", EdgeKind.References);
        AssertEdge(graph, "file:src/video.c", "file:src/packet.h", EdgeKind.References);
        AssertIncludeCounts(graph, "file:src/audio.c", includeDirectives: 1, includedBy: 0);
        AssertIncludeCounts(graph, "file:src/video.c", includeDirectives: 1, includedBy: 0);
        AssertIncludeCounts(graph, "file:src/packet.h", includeDirectives: 0, includedBy: 2);
        AssertFilePressure(
            graph,
            "file:src/audio.c",
            declaredSymbols: 2,
            publicDeclaredSymbols: 1,
            privateDeclaredSymbols: 1,
            internalDeclaredSymbols: 0,
            functionDefinitions: 2,
            functionDeclarations: 0,
            macros: 0,
            outgoingReferences: 3,
            incomingReferences: 0,
            outgoingCalls: 1,
            incomingCalls: 1);
        AssertFilePressure(
            graph,
            "file:src/packet.h",
            declaredSymbols: 3,
            publicDeclaredSymbols: 2,
            privateDeclaredSymbols: 0,
            internalDeclaredSymbols: 1,
            functionDefinitions: 0,
            functionDeclarations: 0,
            macros: 1,
            outgoingReferences: 0,
            incomingReferences: 6,
            outgoingCalls: 0,
            incomingCalls: 0);
        AssertFilePressure(
            graph,
            "file:src/video.c",
            declaredSymbols: 2,
            publicDeclaredSymbols: 1,
            privateDeclaredSymbols: 1,
            internalDeclaredSymbols: 0,
            functionDefinitions: 2,
            functionDeclarations: 0,
            macros: 0,
            outgoingReferences: 3,
            incomingReferences: 0,
            outgoingCalls: 1,
            incomingCalls: 1);
        AssertCall(graph, "method:decode_audio(Packet*)", "method:audio_gain(int)");
        AssertCall(graph, "method:decode_video(Packet*)", "method:video_scale(int)");
        AssertDirectCallCounts(graph, "method:decode_audio(Packet*)", incoming: 0, outgoing: 1);
        AssertDirectCallCounts(graph, "method:audio_gain(int)", incoming: 1, outgoing: 0);
        AssertDirectCallCounts(graph, "method:decode_video(Packet*)", incoming: 0, outgoing: 1);
        AssertDirectCallCounts(graph, "method:video_scale(int)", incoming: 1, outgoing: 0);
        AssertReferenceKind(graph, "method:decode_audio(Packet*)", "type:Packet", "parameterType");
        AssertReferenceKind(graph, "method:decode_video(Packet*)", "type:Packet", "parameterType");
        AssertAllNativeFactsCarryBuildProfile(graph, "multi-debug");
    }

    [SkippableFact]
    public void Executable_PartialFixture_CanEmitGraphMarkedPartial()
    {
        var graph = RunFixture("partial-parse-c", "partial-debug", allowPartial: true).Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        AssertModuleParseHealth(graph, "mod:partial-parse-c", total: 2, parsed: 1, failed: 1);
        AssertModuleFileInventory(graph, "mod:partial-parse-c", translationUnits: 2, headers: 1);
        AssertTranslationUnitHealth(graph, "file:src/good.c", "parsed");
        AssertTranslationUnitHealth(graph, "file:src/missing.c", "failed");
        Assert.NotNull(graph.GetSymbol("method:decode_good(Packet*)"));
        Assert.NotNull(graph.GetSymbol("method:normalize(int)"));
        AssertCall(graph, "method:decode_good(Packet*)", "method:normalize(int)");
        AssertAllNativeFactsCarryBuildProfile(graph, "partial-debug");
    }

    [SkippableFact]
    public void Executable_PartialFixture_FailsClosedWithoutAllowPartial()
    {
        var result = RunFixtureProcess("partial-parse-c", "partial-debug");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to parse", result.StandardError);
        Assert.True(
            string.IsNullOrWhiteSpace(result.StandardOutput),
            "Default partial scans must not emit a graph unless --allow-partial is explicit.");
    }

    [SkippableFact]
    public void Executable_WarningFixture_ReportsDiagnosticHealth()
    {
        var graph = RunFixture("warning-c", "warning-debug").Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        AssertModuleDiagnosticHealth(
            graph,
            "mod:warning-c",
            total: 1,
            parsed: 1,
            failed: 0,
            warnings: 1,
            errors: 0,
            fatals: 0);
        AssertTranslationUnitHealth(
            graph,
            "file:src/warning.c",
            "parsed",
            warnings: 1,
            errors: 0,
            fatals: 0);
        AssertTranslationUnitBuildInputs(
            graph,
            "file:src/warning.c",
            defines: 0,
            undefines: 0,
            includePaths: 0);
        Assert.NotNull(graph.GetSymbol("method:warning_value(int)"));
        AssertAllNativeFactsCarryBuildProfile(graph, "warning-debug");
    }

    [SkippableFact]
    public void Executable_CrossTranslationUnitCall_PreservesDefinitionLocation()
    {
        var graph = RunFixture("cross-tu-c", "cross-tu-debug").Graph;

        Assert.Empty(GraphValidator.Validate(graph));
        AssertModuleParseHealth(graph, "mod:cross-tu-c", total: 2, parsed: 2, failed: 0);
        AssertModuleFileInventory(graph, "mod:cross-tu-c", translationUnits: 2, headers: 1);

        var decodeAudio = graph.GetSymbol("method:decode_audio(Packet*)");
        Assert.NotNull(decodeAudio);
        Assert.Equal("src/audio.c", decodeAudio!.FilePath);
        Assert.Equal("definition", decodeAudio.Properties["native.declarationKind"]);

        var decodeVideo = graph.GetSymbol("method:decode_video(Packet*)");
        Assert.NotNull(decodeVideo);
        Assert.Equal("src/video.c", decodeVideo!.FilePath);
        Assert.Equal("definition", decodeVideo.Properties["native.declarationKind"]);

        AssertCall(graph, "method:decode_video(Packet*)", "method:decode_audio(Packet*)");
        AssertDirectCallCounts(graph, "method:decode_video(Packet*)", incoming: 0, outgoing: 1);
        AssertDirectCallCounts(graph, "method:decode_audio(Packet*)", incoming: 1, outgoing: 0);
        AssertCrossFileDirectCallCounts(graph, "method:decode_video(Packet*)", incoming: 0, outgoing: 1);
        AssertCrossFileDirectCallCounts(graph, "method:decode_audio(Packet*)", incoming: 1, outgoing: 0);
        AssertFilePressure(
            graph,
            "file:src/audio.c",
            declaredSymbols: 1,
            publicDeclaredSymbols: 1,
            privateDeclaredSymbols: 0,
            internalDeclaredSymbols: 0,
            functionDefinitions: 1,
            functionDeclarations: 0,
            macros: 0,
            outgoingReferences: 3,
            incomingReferences: 0,
            outgoingCalls: 0,
            incomingCalls: 1,
            outgoingCrossFileCalls: 0,
            incomingCrossFileCalls: 1);
        AssertFilePressure(
            graph,
            "file:src/video.c",
            declaredSymbols: 1,
            publicDeclaredSymbols: 1,
            privateDeclaredSymbols: 0,
            internalDeclaredSymbols: 0,
            functionDefinitions: 1,
            functionDeclarations: 0,
            macros: 0,
            outgoingReferences: 2,
            incomingReferences: 0,
            outgoingCalls: 1,
            incomingCalls: 0,
            outgoingCrossFileCalls: 1,
            incomingCrossFileCalls: 0);
        AssertAllNativeFactsCarryBuildProfile(graph, "cross-tu-debug");
    }

    private static GraphDocument RunFixture(
        string fixtureName,
        string profile,
        string? compilationDatabase = null,
        bool allowPartial = false)
    {
        var result = RunFixtureProcess(fixtureName, profile, compilationDatabase, allowPartial);

        Assert.True(
            result.ExitCode == 0,
            $"Native Clang executable failed with exit code {result.ExitCode}:{Environment.NewLine}{result.StandardError}");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
        return new JsonGraphImporter().ImportDocument(stream);
    }

    private static NativeRunResult RunFixtureProcess(
        string fixtureName,
        string profile,
        string? compilationDatabase = null,
        bool allowPartial = false)
    {
        var repoRoot = FindRepoRoot();
        var executable = NativeExecutablePath(repoRoot);
        Skip.IfNot(
            File.Exists(executable),
            $"Native Clang executable not found at {executable}. Build adapters/native-clang first.");

        var fixtureRoot = Path.Combine(
            repoRoot,
            "adapters",
            "native-clang",
            "test-fixtures",
            fixtureName);
        var compileDatabaseRoot = compilationDatabase is null
            ? fixtureRoot
            : Path.Combine(fixtureRoot, compilationDatabase);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(fixtureRoot);
        start.ArgumentList.Add("--compilation-database");
        start.ArgumentList.Add(compileDatabaseRoot);
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(profile);
        if (allowPartial)
            start.ArgumentList.Add("--allow-partial");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start native Clang executable.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new NativeRunResult(process.ExitCode, stdout, stderr);
    }

    private static string NativeExecutablePath(string repoRoot)
    {
        var basePath = Path.Combine(
            repoRoot,
            "artifacts",
            "native-clang-build",
            "lifeblood-native-clang");
        return OperatingSystem.IsWindows() ? basePath + ".exe" : basePath;
    }

    private static void AssertCall(SemanticGraph graph, string sourceId, string targetId)
    {
        var edge = AssertEdge(graph, sourceId, targetId, EdgeKind.Calls);
        Assert.Equal("direct", edge.Properties["native.callKind"]);
        AssertUsableEvidence(edge);
    }

    private static void AssertReferenceKind(
        SemanticGraph graph,
        string sourceId,
        string targetId,
        string referenceKind)
    {
        var edge = AssertEdge(graph, sourceId, targetId, EdgeKind.References);
        Assert.Equal(referenceKind, edge.Properties["native.referenceKind"]);
        AssertUsableEvidence(edge);
    }

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

    private static void AssertUsableEvidence(Edge edge)
    {
        Assert.NotEqual(EvidenceKind.Inferred, edge.Evidence.Kind);
        Assert.Equal(ConfidenceLevel.Proven, edge.Evidence.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(edge.Evidence.SourceSpan));
    }

    private static void AssertAllNativeFactsCarryBuildProfile(SemanticGraph graph, string profile)
    {
        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Properties.ContainsKey("native.kind"))
                Assert.Equal(profile, symbol.Properties["native.buildProfile"]);
        }

        foreach (var edge in graph.Edges)
        {
            if (edge.Properties.Keys.Any(key => key.StartsWith("native.", StringComparison.Ordinal)))
                Assert.Equal(profile, edge.Properties["native.buildProfile"]);
        }
    }

    private static void AssertModuleParseHealth(
        SemanticGraph graph,
        string moduleId,
        int total,
        int parsed,
        int failed)
        => AssertModuleDiagnosticHealth(
            graph,
            moduleId,
            total,
            parsed,
            failed,
            warnings: 0,
            errors: 0,
            fatals: 0);

    private static void AssertModuleDiagnosticHealth(
        SemanticGraph graph,
        string moduleId,
        int total,
        int parsed,
        int failed,
        int warnings,
        int errors,
        int fatals)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(total.ToString(), module!.Properties["native.translationUnitCount"]);
        Assert.Equal(parsed.ToString(), module.Properties["native.parsedTranslationUnitCount"]);
        Assert.Equal(failed.ToString(), module.Properties["native.failedTranslationUnitCount"]);
        Assert.Equal(warnings.ToString(), module.Properties["native.warningDiagnosticCount"]);
        Assert.Equal(errors.ToString(), module.Properties["native.errorDiagnosticCount"]);
        Assert.Equal(fatals.ToString(), module.Properties["native.fatalDiagnosticCount"]);
        Assert.Equal(failed == 0 ? "complete" : "partial", module.Properties["native.parseStatus"]);
    }

    private static void AssertTranslationUnitHealth(
        SemanticGraph graph,
        string fileId,
        string parseStatus,
        int warnings = 0,
        int errors = 0,
        int fatals = 0)
    {
        var file = graph.GetSymbol(fileId);
        Assert.NotNull(file);
        Assert.Equal("true", file!.Properties["native.translationUnit"]);
        Assert.Equal(parseStatus, file.Properties["native.parseStatus"]);
        Assert.Equal(warnings.ToString(), file.Properties["native.warningDiagnosticCount"]);
        Assert.Equal(errors.ToString(), file.Properties["native.errorDiagnosticCount"]);
        Assert.Equal(fatals.ToString(), file.Properties["native.fatalDiagnosticCount"]);
    }

    private static void AssertTranslationUnitBuildInputs(
        SemanticGraph graph,
        string fileId,
        int defines,
        int undefines,
        int includePaths = 1,
        int systemIncludePaths = 0,
        int quoteIncludePaths = 0,
        string? languageStandard = null)
    {
        var file = graph.GetSymbol(fileId);
        Assert.NotNull(file);
        Assert.True(int.Parse(file!.Properties["native.parseArgumentCount"]) > 0);
        Assert.Equal(defines.ToString(), file.Properties["native.commandLineDefineCount"]);
        Assert.Equal(undefines.ToString(), file.Properties["native.commandLineUndefineCount"]);
        Assert.Equal(includePaths.ToString(), file.Properties["native.includeSearchPathCount"]);
        Assert.Equal(
            systemIncludePaths.ToString(),
            file.Properties["native.systemIncludeSearchPathCount"]);
        Assert.Equal(
            quoteIncludePaths.ToString(),
            file.Properties["native.quoteIncludeSearchPathCount"]);
        Assert.Equal("c", file.Properties["native.sourceLanguage"]);
        if (languageStandard is not null)
            Assert.Equal(languageStandard, file.Properties["native.languageStandard"]);
    }

    private static void AssertModuleFileInventory(
        SemanticGraph graph,
        string moduleId,
        int translationUnits,
        int headers)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(translationUnits.ToString(), module!.Properties["native.translationUnitFileCount"]);
        Assert.Equal(headers.ToString(), module.Properties["native.headerFileCount"]);
    }

    private static void AssertModuleGraphInventory(
        SemanticGraph graph,
        string moduleId,
        int symbols,
        int edges,
        int references,
        int calls)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(symbols.ToString(), module!.Properties["native.symbolCount"]);
        Assert.Equal(edges.ToString(), module.Properties["native.edgeCount"]);
        Assert.Equal(references.ToString(), module.Properties["native.referenceEdgeCount"]);
        Assert.Equal(calls.ToString(), module.Properties["native.callEdgeCount"]);
    }

    private static void AssertModuleVisibilityInventory(
        SemanticGraph graph,
        string moduleId,
        int publicSymbols,
        int privateSymbols,
        int internalSymbols)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(publicSymbols.ToString(), module!.Properties["native.publicSymbolCount"]);
        Assert.Equal(privateSymbols.ToString(), module.Properties["native.privateSymbolCount"]);
        Assert.Equal(internalSymbols.ToString(), module.Properties["native.internalSymbolCount"]);
    }

    private static void AssertModuleFunctionInventory(
        SemanticGraph graph,
        string moduleId,
        int definitions,
        int declarations)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(definitions.ToString(), module!.Properties["native.functionDefinitionCount"]);
        Assert.Equal(declarations.ToString(), module.Properties["native.functionDeclarationCount"]);
    }

    private static void AssertModuleNativeKindInventory(
        SemanticGraph graph,
        string moduleId,
        int macros,
        int callbackTables = 0)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(macros.ToString(), module!.Properties["native.macroCount"]);
        Assert.Equal(callbackTables.ToString(), module.Properties["native.callbackTableCount"]);
    }

    private static void AssertFileNativeKindInventory(
        SemanticGraph graph,
        string fileId,
        int macros,
        int callbackTables)
    {
        var file = graph.GetSymbol(fileId);
        Assert.NotNull(file);
        Assert.Equal(macros.ToString(), file!.Properties["native.fileMacroCount"]);
        Assert.Equal(callbackTables.ToString(), file.Properties["native.fileCallbackTableCount"]);
    }

    private static void AssertModuleUndefines(
        SemanticGraph graph,
        string moduleId,
        string undefines)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(undefines, module!.Properties["native.undefines"]);
    }

    private static void AssertModuleBuildFacts(
        SemanticGraph graph,
        string moduleId,
        int includePaths,
        int systemIncludePaths,
        int quoteIncludePaths,
        string sourceLanguages,
        string? languageStandards = null)
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(includePaths.ToString(), module!.Properties["native.includeSearchPathCount"]);
        Assert.Equal(
            systemIncludePaths.ToString(),
            module.Properties["native.systemIncludeSearchPathCount"]);
        Assert.Equal(
            quoteIncludePaths.ToString(),
            module.Properties["native.quoteIncludeSearchPathCount"]);
        Assert.Equal(sourceLanguages, module.Properties["native.sourceLanguages"]);
        if (languageStandards is not null)
            Assert.Equal(languageStandards, module.Properties["native.languageStandards"]);
    }

    private static void AssertIncludeCounts(
        SemanticGraph graph,
        string fileId,
        int includeDirectives,
        int includedBy)
    {
        var file = graph.GetSymbol(fileId);
        Assert.NotNull(file);
        Assert.Equal(
            includeDirectives.ToString(),
            file!.Properties.GetValueOrDefault("native.includeDirectiveCount", "0"));
        Assert.Equal(
            includedBy.ToString(),
            file.Properties.GetValueOrDefault("native.includedByCount", "0"));
    }

    private static void AssertFilePressure(
        SemanticGraph graph,
        string fileId,
        int declaredSymbols,
        int publicDeclaredSymbols,
        int privateDeclaredSymbols,
        int internalDeclaredSymbols,
        int functionDefinitions,
        int functionDeclarations,
        int macros,
        int outgoingReferences,
        int incomingReferences,
        int outgoingCalls,
        int incomingCalls,
        int outgoingCrossFileCalls = 0,
        int incomingCrossFileCalls = 0)
    {
        var file = graph.GetSymbol(fileId);
        Assert.NotNull(file);
        Assert.Equal(declaredSymbols.ToString(), file!.Properties["native.declaredSymbolCount"]);
        Assert.Equal(
            publicDeclaredSymbols.ToString(),
            file.Properties["native.publicDeclaredSymbolCount"]);
        Assert.Equal(
            privateDeclaredSymbols.ToString(),
            file.Properties["native.privateDeclaredSymbolCount"]);
        Assert.Equal(
            internalDeclaredSymbols.ToString(),
            file.Properties["native.internalDeclaredSymbolCount"]);
        Assert.Equal(
            functionDefinitions.ToString(),
            file.Properties["native.fileFunctionDefinitionCount"]);
        Assert.Equal(
            functionDeclarations.ToString(),
            file.Properties["native.fileFunctionDeclarationCount"]);
        Assert.Equal(macros.ToString(), file.Properties["native.fileMacroCount"]);
        Assert.Equal("0", file.Properties["native.fileCallbackTableCount"]);
        Assert.Equal(
            outgoingReferences.ToString(),
            file.Properties["native.fileOutgoingReferenceEdgeCount"]);
        Assert.Equal(
            incomingReferences.ToString(),
            file.Properties["native.fileIncomingReferenceEdgeCount"]);
        Assert.Equal(outgoingCalls.ToString(), file.Properties["native.fileOutgoingCallEdgeCount"]);
        Assert.Equal(incomingCalls.ToString(), file.Properties["native.fileIncomingCallEdgeCount"]);
        Assert.Equal(
            outgoingCrossFileCalls.ToString(),
            file.Properties["native.fileOutgoingCrossFileCallEdgeCount"]);
        Assert.Equal(
            incomingCrossFileCalls.ToString(),
            file.Properties["native.fileIncomingCrossFileCallEdgeCount"]);
    }

    private static void AssertDirectCallCounts(
        SemanticGraph graph,
        string methodId,
        int incoming,
        int outgoing)
    {
        var method = graph.GetSymbol(methodId);
        Assert.NotNull(method);
        Assert.Equal(
            incoming.ToString(),
            method!.Properties.GetValueOrDefault("native.directCallInCount", "0"));
        Assert.Equal(
            outgoing.ToString(),
            method.Properties.GetValueOrDefault("native.directCallOutCount", "0"));
    }

    private static void AssertReferenceCounts(
        SemanticGraph graph,
        string symbolId,
        int incoming,
        int outgoing)
    {
        var symbol = graph.GetSymbol(symbolId);
        Assert.NotNull(symbol);
        Assert.Equal(
            incoming.ToString(),
            symbol!.Properties.GetValueOrDefault("native.referenceInCount", "0"));
        Assert.Equal(
            outgoing.ToString(),
            symbol.Properties.GetValueOrDefault("native.referenceOutCount", "0"));
    }

    private static void AssertCrossFileDirectCallCounts(
        SemanticGraph graph,
        string symbolId,
        int incoming,
        int outgoing)
    {
        var symbol = graph.GetSymbol(symbolId);
        Assert.NotNull(symbol);
        Assert.Equal(
            incoming.ToString(),
            symbol!.Properties.GetValueOrDefault("native.crossFileDirectCallInCount", "0"));
        Assert.Equal(
            outgoing.ToString(),
            symbol.Properties.GetValueOrDefault("native.crossFileDirectCallOutCount", "0"));
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

    private sealed record NativeRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
