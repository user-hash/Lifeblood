using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.Git;

/// <summary>
/// Git-backed source-control evidence adapter. Every capture is rooted at the
/// caller's workspace and all process output is drained with explicit bounds.
/// </summary>
public sealed partial class GitSourceControlSnapshotProvider : ISourceControlSnapshotProvider
{
    private const int MaxOutputChars = 65_536;
    private const int MaxFailureChars = 512;
    private const int MaxDirtyEntrySample = 32;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public SourceControlSnapshot Capture(string? startPath)
    {
        var start = NormalizeStartPath(startPath);
        if (start == null)
        {
            return SourceControlSnapshot.Unavailable(
                startPath,
                "repositoryNotFound",
                "The source-control start path does not exist.");
        }

        var rootResult = Run(start.WorkingDirectory, "rev-parse", "--show-toplevel");
        if (!rootResult.Success)
        {
            var source = rootResult.FailureKind switch
            {
                GitFailureKind.Unavailable => "gitUnavailable",
                GitFailureKind.NotRepository => "repositoryNotFound",
                _ => "gitFailed",
            };
            return SourceControlSnapshot.Unavailable(
                start.AttemptedPath,
                source,
                BoundFailure(rootResult.Error));
        }

        var repositoryRoot = Path.GetFullPath(
            rootResult.Output.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        if (repositoryRoot.Length == 0 || !Directory.Exists(repositoryRoot))
        {
            return SourceControlSnapshot.Unavailable(
                start.AttemptedPath,
                "gitFailed",
                "Git returned an invalid repository root.");
        }

        var commitResult = Run(repositoryRoot, "rev-parse", "HEAD");
        var statusResult = Run(repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all");
        var tagResult = Run(repositoryRoot, "tag", "--merged", "HEAD", "--list", "v*");

        var commitHash = commitResult.Success ? commitResult.Output.Trim() : "";
        var dirtyEntries = statusResult.Success
            ? ParseDirtyEntries(statusResult.Output)
            : Array.Empty<string>();
        var dirty = statusResult.Success ? dirtyEntries.Length > 0 : (bool?)null;
        var failures = new[] { commitResult, statusResult, tagResult }
            .Where(result => !result.Success)
            .Select(result => result.Error)
            .Where(error => !string.IsNullOrWhiteSpace(error));

        return new SourceControlSnapshot(
            AttemptedPath: start.AttemptedPath,
            RepositoryRoot: repositoryRoot,
            CommitHash: commitHash,
            ShortCommitHash: commitHash[..Math.Min(12, commitHash.Length)],
            Dirty: dirty,
            State: dirty == true ? "dirty" : dirty == false ? "clean" : "unknown",
            Source: commitResult.Success && statusResult.Success && tagResult.Success ? "git" : "gitFailed",
            LatestSemanticVersionTag: tagResult.Success ? FindLatestStableTag(tagResult.Output) : "",
            DirtyEntryCount: dirtyEntries.Length,
            DirtyEntryCountCapped: statusResult.OutputTruncated,
            DirtyEntries: dirtyEntries.Take(MaxDirtyEntrySample).ToArray(),
            DirtyEntriesTruncated: statusResult.OutputTruncated || dirtyEntries.Length > MaxDirtyEntrySample,
            FailureReason: BoundFailure(string.Join("; ", failures)));
    }

    private static SourceControlStart? NormalizeStartPath(string? startPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(startPath) ? Environment.CurrentDirectory : startPath);
            if (Directory.Exists(fullPath)) return new SourceControlStart(fullPath, fullPath);
            if (File.Exists(fullPath))
            {
                var parent = Path.GetDirectoryName(fullPath);
                return parent == null ? null : new SourceControlStart(fullPath, parent);
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string FindLatestStableTag(string output)
    {
        Version? latestVersion = null;
        var latestTag = "";
        foreach (var candidate in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = StableSemanticVersionTag().Match(candidate);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version)) continue;
            if (latestVersion != null && version <= latestVersion) continue;
            latestVersion = version;
            latestTag = candidate;
        }

        return latestTag;
    }

    private static string[] ParseDirtyEntries(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length > 3 ? line[3..] : line)
            .ToArray();

    private static GitCommandResult Run(string workingDirectory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["LC_ALL"] = "C";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process == null)
            {
                return GitCommandResult.Failed(GitFailureKind.Failed, "Git did not start.");
            }
            // The MCP host's stdin is a persistent JSON-RPC pipe. Git must never
            // inherit that open handle or a non-interactive command can wait for
            // the server lifetime instead of exiting.
            process.StandardInput.Close();

            // Roslyn can saturate the ThreadPool immediately before evidence
            // capture. Dedicated drain workers plus synchronous process waiting
            // avoid both pipe-buffer deadlock and ThreadPool-continuation starvation.
            var outputTask = StartBoundedDrain(process.StandardOutput, MaxOutputChars);
            var errorTask = StartBoundedDrain(process.StandardError, MaxFailureChars);
            if (!process.WaitForExit(checked((int)CommandTimeout.TotalMilliseconds)))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(1000); } catch { }
                return GitCommandResult.Failed(GitFailureKind.Failed, "Git command timed out.");
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode == 0)
            {
                return new GitCommandResult(
                    Success: true,
                    Output: output.Text.TrimEnd(),
                    OutputTruncated: output.Truncated,
                    FailureKind: GitFailureKind.None,
                    Error: "");
            }

            var failureKind = error.Text.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? GitFailureKind.NotRepository
                : GitFailureKind.Failed;
            return GitCommandResult.Failed(failureKind, error.Text);
        }
        catch (Win32Exception ex)
        {
            return GitCommandResult.Failed(GitFailureKind.Unavailable, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return GitCommandResult.Failed(GitFailureKind.Failed, ex.Message);
        }
    }

    private static Task<BoundedText> StartBoundedDrain(StreamReader reader, int maximumChars)
        => Task.Factory.StartNew(
            () => ReadBounded(reader, maximumChars),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static BoundedText ReadBounded(StreamReader reader, int maximumChars)
    {
        var buffer = new char[4096];
        var text = new StringBuilder(Math.Min(maximumChars, 4096));
        var truncated = false;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var remaining = maximumChars - text.Length;
            if (remaining > 0) text.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }

        return new BoundedText(text.ToString(), truncated);
    }

    private static string BoundFailure(string failure)
    {
        var normalized = failure.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized[..Math.Min(MaxFailureChars, normalized.Length)];
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex StableSemanticVersionTag();

    private enum GitFailureKind
    {
        None,
        Unavailable,
        NotRepository,
        Failed,
    }

    private sealed record GitCommandResult(
        bool Success,
        string Output,
        bool OutputTruncated,
        GitFailureKind FailureKind,
        string Error)
    {
        public static GitCommandResult Failed(GitFailureKind failureKind, string error)
            => new(false, "", false, failureKind, error);
    }

    private sealed record BoundedText(string Text, bool Truncated);

    private sealed record SourceControlStart(string AttemptedPath, string WorkingDirectory);
}
