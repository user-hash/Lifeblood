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
        var committedGraph = session.Graph;
        var committedGeneration = session.AnalysisGeneration;

        Assert.Throws<JsonException>(() =>
            session.Load(candidateRoot, graphPath: null, rulesPath: invalidRules));

        Assert.Same(committedGraph, session.Graph);
        Assert.Equal(committedGeneration, session.AnalysisGeneration);
        Assert.Equal(firstRoot, session.ProjectRoot);
        Assert.Equal(firstRoot, session.CurrentWorkspaceContext?.RootPath);
        Assert.True(session.CanIncremental);
        Assert.True(session.HasCompilationState);
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
