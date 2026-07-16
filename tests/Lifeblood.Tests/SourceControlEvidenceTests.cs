using System.Diagnostics;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.Git;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end Git adapter receipts. Real temporary repositories verify the
/// production process boundary, analyzed-root precedence, and evidence bounds.
/// </summary>
public sealed class SourceControlEvidenceTests
{
    [Fact]
    public void Capture_UsesCallerWorkspaceAndBoundsDirtyDetails()
    {
        using var repository = TemporaryGitRepository.Create();
        var provider = new GitSourceControlSnapshotProvider();

        var snapshot = provider.Capture(repository.GraphPath);

        Assert.Equal(repository.Root, snapshot.RepositoryRoot);
        Assert.Equal(40, snapshot.CommitHash.Length);
        Assert.Equal(snapshot.CommitHash[..12], snapshot.ShortCommitHash);
        Assert.True(snapshot.Dirty);
        Assert.Equal("dirty", snapshot.State);
        Assert.Equal("git", snapshot.Source);
        Assert.Equal("v0.7.12", snapshot.LatestSemanticVersionTag);
        Assert.Equal(40, snapshot.DirtyEntryCount);
        Assert.False(snapshot.DirtyEntryCountCapped);
        Assert.Equal(32, snapshot.DirtyEntries.Length);
        Assert.True(snapshot.DirtyEntriesTruncated);
        Assert.All(snapshot.DirtyEntries, entry => Assert.StartsWith("dirty-", entry));
        Assert.Empty(snapshot.FailureReason);
    }

    [Fact]
    public void Capture_SelectsNewestFourPartStableSemanticTag()
    {
        using var repository = TemporaryGitRepository.Create(
            "v1.2.356",
            "v1.2.376.0",
            "v1.2.377.0-preview.1");
        var provider = new GitSourceControlSnapshotProvider();

        var snapshot = provider.Capture(repository.Root);

        Assert.Equal("v1.2.376.0", snapshot.LatestSemanticVersionTag);
    }

    [Fact]
    public void AnalyzeEvidenceReceipt_UsesAnalyzedProjectRepository()
    {
        using var repository = TemporaryGitRepository.Create();
        var snapshot = ServerIdentity.CaptureAnalyzeSourceControl(
            new GitSourceControlSnapshotProvider(),
            repository.NestedDirectory,
            graphPath: null);
        var receipt = ServerIdentity.BuildAnalyzeEvidenceReceipt(
            mode: "full",
            requestedMode: null,
            graph: new SemanticGraph(),
            analysis: null,
            projectPath: repository.NestedDirectory,
            graphPath: null,
            rulesPath: null,
            activeProfiles: null,
            fallbackReason: null,
            sourceControl: snapshot);

        var sourceControl = Serialize(receipt).GetProperty("sourceControl");

        Assert.Equal(repository.NestedDirectory, sourceControl.GetProperty("attemptedPath").GetString());
        Assert.Equal(repository.Root, sourceControl.GetProperty("repositoryRoot").GetString());
        Assert.Equal("git", sourceControl.GetProperty("source").GetString());
    }

    [Fact]
    public void GraphSessionLoad_UsesGraphPathRepositoryAtPublicAnalyzeBoundary()
    {
        using var repository = TemporaryGitRepository.Create();
        using var session = new GraphSession(
            new PhysicalFileSystem(),
            sourceControl: new GitSourceControlSnapshotProvider());
        var response = session.Load(
            projectPath: null,
            graphPath: repository.GraphPath,
            rulesPath: null);

        using var document = JsonDocument.Parse(response);
        var sourceControl = document.RootElement
            .GetProperty("evidenceReceipt")
            .GetProperty("sourceControl");

        Assert.Equal(repository.GraphPath, sourceControl.GetProperty("attemptedPath").GetString());
        Assert.Equal(repository.Root, sourceControl.GetProperty("repositoryRoot").GetString());
    }

    [Fact]
    public void Capture_NonRepositoryReportsRepositoryNotFound()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lifeblood-git-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var snapshot = new GitSourceControlSnapshotProvider().Capture(directory);

            Assert.Equal(directory, snapshot.AttemptedPath);
            Assert.Equal("repositoryNotFound", snapshot.Source);
            Assert.Equal("unknown", snapshot.State);
            Assert.Null(snapshot.Dirty);
            Assert.Empty(snapshot.RepositoryRoot);
            Assert.NotEmpty(snapshot.FailureReason);
            Assert.True(snapshot.FailureReason.Length <= 512);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement Serialize(object? value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private sealed class TemporaryGitRepository : IDisposable
    {
        private TemporaryGitRepository(string root)
        {
            Root = root;
            NestedDirectory = Path.Combine(root, "nested");
            GraphPath = Path.Combine(NestedDirectory, "graph.json");
        }

        public string Root { get; }

        public string NestedDirectory { get; }

        public string GraphPath { get; }

        public static TemporaryGitRepository Create(params string[] additionalTags)
        {
            var root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "lifeblood-git-evidence-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            var graph = new GraphBuilder()
                .AddSymbol(new Symbol { Id = "mod:Fixture", Name = "Fixture", Kind = SymbolKind.Module })
                .Build();
            var document = new GraphDocument
            {
                Language = "test",
                Adapter = new AdapterCapability { CanDiscoverSymbols = true },
                Graph = graph,
            };
            using (var stream = File.Create(Path.Combine(root, "nested", "graph.json")))
            {
                new JsonGraphExporter().Export(document, stream);
            }

            RunGit(root, "init", "--quiet");
            RunGit(root, "add", ".");
            RunGit(root, "-c", "user.name=Lifeblood Tests", "-c", "user.email=tests@lifeblood.local",
                "commit", "--quiet", "-m", "initial");
            RunGit(root, "tag", "v0.7.11");
            RunGit(root, "tag", "v0.7.12");
            RunGit(root, "tag", "v9.0.0-preview.1");
            foreach (var tag in additionalTags)
            {
                RunGit(root, "tag", tag);
            }

            for (var index = 0; index < 40; index++)
            {
                File.WriteAllText(Path.Combine(root, $"dirty-{index:D2}.txt"), index.ToString());
            }

            return new TemporaryGitRepository(root);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(Root, recursive: true);
        }

        private static void RunGit(string workingDirectory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed ({process.ExitCode}).\n{output}\n{error}");
        }
    }
}
