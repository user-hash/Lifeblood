using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-SNAPSHOT-ATOMIC-PUBLISH-001.</summary>
public sealed class GraphSessionPublicationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"lifeblood-publish-{Guid.NewGuid():N}");

    public GraphSessionPublicationTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void Load_FullCandidateRuleFailure_DoesNotPublishAdapterOrWorkspaceFields()
    {
        var firstRoot = CreateProject("First", "namespace First; public class Stable { }");
        var candidateRoot = CreateProject("Candidate", "namespace Candidate; public class Rejected { }");
        var invalidRules = Path.Combine(_tempDir, "invalid-rules.json");
        File.WriteAllText(invalidRules, "{");
        using var session = new GraphSession(new PhysicalFileSystem());

        _ = session.Load(firstRoot, graphPath: null, rulesPath: null);
        var committedSnapshot = session.CurrentSnapshot;
        var committedGraph = session.Graph;
        var committedGeneration = session.AnalysisGeneration;

        Assert.Throws<JsonException>(() =>
            session.Load(candidateRoot, graphPath: null, rulesPath: invalidRules));

        Assert.Same(committedGraph, session.Graph);
        Assert.Same(committedSnapshot, session.CurrentSnapshot);
        Assert.Equal(committedGeneration, session.AnalysisGeneration);
        Assert.Equal(firstRoot, session.ProjectRoot);
        Assert.Equal(firstRoot, session.CurrentWorkspaceContext?.RootPath);
        Assert.True(session.CanIncremental);
        Assert.True(session.HasCompilationState);
    }

    [Fact]
    public void Load_IncrementalCandidateRuleFailure_LeavesCommittedAdapterRetryable()
    {
        var root = CreateProject("Incremental", "namespace Incremental; public class Stable { }");
        var sourcePath = Path.Combine(root, "Incremental.cs");
        var invalidRules = Path.Combine(_tempDir, "invalid-incremental-rules.json");
        File.WriteAllText(invalidRules, "{");
        using var session = new GraphSession(new PhysicalFileSystem());

        _ = session.Load(root, graphPath: null, rulesPath: null);
        var committedSnapshot = session.CurrentSnapshot;
        var committedGraph = session.Graph;
        var committedGeneration = session.AnalysisGeneration;

        File.WriteAllText(sourcePath, "namespace Incremental; public class Refreshed { }");
        Assert.Throws<JsonException>(() => session.Load(
            root,
            graphPath: null,
            rulesPath: invalidRules,
            incremental: true,
            authoritativeChangedFiles: new[] { sourcePath }));

        Assert.Same(committedGraph, session.Graph);
        Assert.Same(committedSnapshot, session.CurrentSnapshot);
        Assert.Equal(committedGeneration, session.AnalysisGeneration);
        Assert.NotNull(session.Graph?.GetSymbol("type:Incremental.Stable"));
        Assert.Null(session.Graph?.GetSymbol("type:Incremental.Refreshed"));

        var retryJson = session.Load(
            root,
            graphPath: null,
            rulesPath: null,
            incremental: true,
            authoritativeChangedFiles: new[] { sourcePath });
        using var retry = JsonDocument.Parse(retryJson);

        Assert.Equal("incremental", retry.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, retry.RootElement.GetProperty("changedSourceFiles").GetInt32());
        Assert.Equal(committedGeneration + 1, session.AnalysisGeneration);
        Assert.Null(session.Graph?.GetSymbol("type:Incremental.Stable"));
        Assert.NotNull(session.Graph?.GetSymbol("type:Incremental.Refreshed"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CreateProject(string name, string source)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, $"{name}.cs"), source);
        return root;
    }
}
