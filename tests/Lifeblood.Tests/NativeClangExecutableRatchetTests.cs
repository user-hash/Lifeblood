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

        var blast = BlastRadiusAnalyzer.Analyze(graph, "method:decode_audio(Packet*)");
        Assert.Contains("field:codec_table", blast.AffectedSymbolIds);
        Assert.Contains("method:dispatch_first(Packet*)", blast.AffectedSymbolIds);
        AssertAllNativeFactsCarryBuildProfile(graph, "callback-debug");
    }

    private static GraphDocument RunFixture(
        string fixtureName,
        string profile,
        string? compilationDatabase = null)
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

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start native Clang executable.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"Native Clang executable failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(stdout));
        return new JsonGraphImporter().ImportDocument(stream);
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
    {
        var module = graph.GetSymbol(moduleId);
        Assert.NotNull(module);
        Assert.Equal(total.ToString(), module!.Properties["native.translationUnitCount"]);
        Assert.Equal(parsed.ToString(), module.Properties["native.parsedTranslationUnitCount"]);
        Assert.Equal(failed.ToString(), module.Properties["native.failedTranslationUnitCount"]);
        Assert.Equal(failed == 0 ? "complete" : "partial", module.Properties["native.parseStatus"]);
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
