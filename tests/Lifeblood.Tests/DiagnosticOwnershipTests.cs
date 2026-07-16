using System.Diagnostics;
using Lifeblood.Adapters.Git;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class DiagnosticOwnershipTests
{
    [Fact]
    public void Classify_UsesChangedLinesAndFailsClosedWithoutEvidence()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ownership-classifier"));
        var changedPath = Path.Combine(root, "Changed.cs");
        var touchedPath = Path.Combine(root, "Touched.cs");
        var unrelatedPath = Path.Combine(root, "Unrelated.cs");
        var externalPath = Path.GetFullPath(Path.Combine(root, "..", "External.cs"));
        var diagnostics = new[]
        {
            Diagnostic("CS0001", changedPath, 4),
            Diagnostic("CS0002", touchedPath, 9),
            Diagnostic("CS0003", unrelatedPath, 2),
            Diagnostic("CS0004", "", 0),
            Diagnostic("CS0005", externalPath, 1),
        };
        var changes = new SourceChangeSnapshot(
            SourceChangeScope.WorkingTree,
            root,
            "base",
            "current",
            "git",
            EvidenceComplete: true,
            OutputTruncated: false,
            Files: new[]
            {
                new SourceChangedFile(
                    changedPath,
                    "Changed.cs",
                    IsNew: false,
                    HasLineEvidence: true,
                    ChangedLines: new[] { new SourceLineRange(3, 5) }),
                new SourceChangedFile(
                    touchedPath,
                    "Touched.cs",
                    IsNew: false,
                    HasLineEvidence: true,
                    ChangedLines: new[] { new SourceLineRange(1, 1) }),
            },
            FailureReason: "");

        var report = DiagnosticOwnershipClassifier.Classify(diagnostics, changes);

        Assert.Equal(DiagnosticOwnership.IntroducedByDiff, report.Classifications[0].Ownership);
        Assert.Equal(DiagnosticOwnership.PreExistingTouchedFile, report.Classifications[1].Ownership);
        Assert.Equal(DiagnosticOwnership.PreExistingUnrelated, report.Classifications[2].Ownership);
        Assert.Equal(DiagnosticOwnership.UnknownOwnership, report.Classifications[3].Ownership);
        Assert.Equal(DiagnosticOwnership.UnknownOwnership, report.Classifications[4].Ownership);

        var unavailable = DiagnosticOwnershipClassifier.Classify(
            diagnostics,
            SourceChangeSnapshot.Unavailable(SourceChangeScope.WorkingTree, "gitFailed"));
        Assert.All(unavailable.Classifications, row =>
            Assert.Equal(DiagnosticOwnership.UnknownOwnership, row.Ownership));
        var stale = DiagnosticOwnershipClassifier.Classify(diagnostics, changes, diagnosticsPossiblyStale: true);
        Assert.All(stale.Classifications, row =>
        {
            Assert.Equal(DiagnosticOwnership.UnknownOwnership, row.Ownership);
            Assert.Equal("diagnosticsPossiblyStale", row.Evidence);
        });
    }

    [Fact]
    public void CaptureChanges_WorkingTreeIncludesTrackedLinesAndUntrackedFiles()
    {
        using var repository = TemporaryRepository.Create();
        File.WriteAllText(repository.TrackedPath, "one\nchanged\nthree\n");
        var untrackedPath = Path.Combine(repository.Root, "Untracked.cs");
        File.WriteAllText(untrackedPath, "class Untracked {}\n");

        var snapshot = new GitSourceControlSnapshotProvider().CaptureChanges(new SourceChangeRequest
        {
            StartPath = repository.Root,
            Scope = SourceChangeScope.WorkingTree,
        });

        Assert.True(snapshot.EvidenceComplete, snapshot.FailureReason);
        Assert.Equal("git", snapshot.Source);
        var tracked = Assert.Single(snapshot.Files, file => file.RepositoryRelativePath == "Tracked.cs");
        Assert.False(tracked.IsNew);
        Assert.Contains(tracked.ChangedLines, range => range.StartLine <= 2 && range.EndLine >= 2);
        var untracked = Assert.Single(snapshot.Files, file => file.RepositoryRelativePath == "Untracked.cs");
        Assert.True(untracked.IsNew);
    }

    [Fact]
    public void CaptureChanges_StagedExcludesUnstagedAndSinceCommitIncludesCurrentTree()
    {
        using var repository = TemporaryRepository.Create();
        File.WriteAllText(repository.TrackedPath, "one\nstaged\nthree\n");
        RunGit(repository.Root, "add", "Tracked.cs");
        File.WriteAllText(repository.OtherPath, "alpha\nunstaged\ngamma\n");

        var provider = new GitSourceControlSnapshotProvider();
        var staged = provider.CaptureChanges(new SourceChangeRequest
        {
            StartPath = repository.Root,
            Scope = SourceChangeScope.Staged,
        });
        var since = provider.CaptureChanges(new SourceChangeRequest
        {
            StartPath = repository.Root,
            Scope = SourceChangeScope.SinceCommit,
            SinceCommit = "HEAD",
        });

        Assert.True(staged.EvidenceComplete, staged.FailureReason);
        Assert.Contains(staged.Files, file => file.RepositoryRelativePath == "Tracked.cs");
        Assert.DoesNotContain(staged.Files, file => file.RepositoryRelativePath == "Other.cs");
        Assert.True(since.EvidenceComplete, since.FailureReason);
        Assert.Contains(since.Files, file => file.RepositoryRelativePath == "Tracked.cs");
        Assert.Contains(since.Files, file => file.RepositoryRelativePath == "Other.cs");
        Assert.Equal(40, since.BaseRevision.Length);
    }

    [Fact]
    public void CaptureChanges_ExplicitFilesCarriesNoInventedLineEvidence()
    {
        using var repository = TemporaryRepository.Create();

        var snapshot = new GitSourceControlSnapshotProvider().CaptureChanges(new SourceChangeRequest
        {
            StartPath = repository.Root,
            Scope = SourceChangeScope.ExplicitFiles,
            TouchedFiles = new[] { "Tracked.cs" },
        });

        Assert.True(snapshot.EvidenceComplete, snapshot.FailureReason);
        Assert.Equal("caller", snapshot.Source);
        var file = Assert.Single(snapshot.Files);
        Assert.False(file.HasLineEvidence);
        var report = DiagnosticOwnershipClassifier.Classify(
            new[] { Diagnostic("CS0001", repository.TrackedPath, 2) },
            snapshot);
        Assert.Equal(DiagnosticOwnership.UnknownOwnership, Assert.Single(report.Classifications).Ownership);
    }

    private static DiagnosticInfo Diagnostic(string id, string path, int line)
        => new()
        {
            Id = id,
            Message = id,
            Severity = DiagnosticSeverity.Warning,
            FilePath = path,
            Line = line,
        };

    private sealed class TemporaryRepository : IDisposable
    {
        private TemporaryRepository(string root)
        {
            Root = root;
            TrackedPath = Path.Combine(root, "Tracked.cs");
            OtherPath = Path.Combine(root, "Other.cs");
        }

        public string Root { get; }
        public string TrackedPath { get; }
        public string OtherPath { get; }

        public static TemporaryRepository Create()
        {
            var root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "lifeblood-diagnostic-ownership-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Tracked.cs"), "one\ntwo\nthree\n");
            File.WriteAllText(Path.Combine(root, "Other.cs"), "alpha\nbeta\ngamma\n");
            RunGit(root, "init", "--quiet");
            RunGit(root, "add", ".");
            RunGit(root, "-c", "user.name=Lifeblood Tests", "-c", "user.email=tests@lifeblood.local",
                "commit", "--quiet", "-m", "baseline");
            return new TemporaryRepository(root);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
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
