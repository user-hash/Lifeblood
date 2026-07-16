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
    private const int MaxDiffOutputChars = 1_048_576;
    private const int MaxFailureChars = 512;
    private const int MaxDirtyEntrySample = 32;
    private const int MaxChangedFiles = 4096;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public SourceControlSnapshot Capture(string? startPath)
    {
        var repository = ResolveRepository(startPath);
        if (!repository.Success)
        {
            return SourceControlSnapshot.Unavailable(
                startPath,
                repository.Source,
                repository.FailureReason);
        }

        var repositoryRoot = repository.RepositoryRoot;
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
            AttemptedPath: repository.AttemptedPath,
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

    public SourceChangeSnapshot CaptureChanges(SourceChangeRequest request)
    {
        var repository = ResolveRepository(request.StartPath);
        if (!repository.Success)
        {
            return SourceChangeSnapshot.Unavailable(
                request.Scope,
                repository.Source,
                repository.FailureReason);
        }

        var currentResult = Run(repository.RepositoryRoot, "rev-parse", "HEAD");
        if (!currentResult.Success)
        {
            return SourceChangeSnapshot.Unavailable(
                request.Scope,
                "gitFailed",
                BoundFailure(currentResult.Error));
        }

        var currentRevision = currentResult.Output.Trim();
        if (request.Scope == SourceChangeScope.ExplicitFiles)
        {
            return CaptureExplicitFiles(repository, currentRevision, request.TouchedFiles);
        }

        var baseRevision = currentRevision;
        string[] diffArguments;
        if (request.Scope == SourceChangeScope.SinceCommit)
        {
            if (string.IsNullOrWhiteSpace(request.SinceCommit))
            {
                return SourceChangeSnapshot.Unavailable(
                    request.Scope,
                    "invalidRequest",
                    "sinceCommit is required for the SinceCommit scope.");
            }

            var baseResult = Run(
                repository.RepositoryRoot,
                "rev-parse",
                "--verify",
                "--end-of-options",
                request.SinceCommit.Trim() + "^{commit}");
            if (!baseResult.Success)
            {
                return SourceChangeSnapshot.Unavailable(
                    request.Scope,
                    "invalidRevision",
                    BoundFailure(baseResult.Error));
            }

            baseRevision = baseResult.Output.Trim();
            diffArguments = BuildDiffArguments(baseRevision);
        }
        else if (request.Scope == SourceChangeScope.Staged)
        {
            diffArguments = BuildDiffArguments("--cached");
        }
        else
        {
            diffArguments = BuildDiffArguments("HEAD");
        }

        var diffResult = RunWithLimit(
            repository.RepositoryRoot,
            MaxDiffOutputChars,
            diffArguments);
        if (!diffResult.Success)
        {
            return SourceChangeSnapshot.Unavailable(
                request.Scope,
                "gitFailed",
                BoundFailure(diffResult.Error));
        }

        var parsed = ParseDiff(repository.RepositoryRoot, diffResult.Output);
        var includeUntracked = request.Scope is SourceChangeScope.WorkingTree or SourceChangeScope.SinceCommit;
        GitCommandResult? untrackedResult = null;
        if (includeUntracked)
        {
            untrackedResult = RunWithLimit(
                repository.RepositoryRoot,
                MaxDiffOutputChars,
                "-c",
                "core.quotePath=false",
                "ls-files",
                "--others",
                "--exclude-standard",
                "-z");
            if (!untrackedResult.Success)
            {
                return SourceChangeSnapshot.Unavailable(
                    request.Scope,
                    "gitFailed",
                    BoundFailure(untrackedResult.Error));
            }
        }

        var files = new Dictionary<string, SourceChangedFile>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in parsed.Files)
        {
            if (!files.TryGetValue(file.FilePath, out var existing))
            {
                files.Add(file.FilePath, file);
                continue;
            }

            files[file.FilePath] = existing with
            {
                IsNew = existing.IsNew || file.IsNew,
                HasLineEvidence = existing.HasLineEvidence && file.HasLineEvidence,
                ChangedLines = existing.ChangedLines
                    .Concat(file.ChangedLines)
                    .Distinct()
                    .OrderBy(range => range.StartLine)
                    .ThenBy(range => range.EndLine)
                    .ToArray(),
            };
        }
        if (untrackedResult != null)
        {
            foreach (var relativePath in untrackedResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryCanonicalizeRepositoryPath(
                        repository.RepositoryRoot,
                        relativePath,
                        out var fullPath,
                        out var normalizedRelative))
                {
                    parsed = parsed with { FailureReason = "Git returned an invalid untracked path." };
                    break;
                }

                files[fullPath] = new SourceChangedFile(
                    fullPath,
                    normalizedRelative,
                    IsNew: true,
                    HasLineEvidence: true,
                    ChangedLines: Array.Empty<SourceLineRange>());
            }
        }

        var outputTruncated = diffResult.OutputTruncated || untrackedResult?.OutputTruncated == true;
        var tooManyFiles = files.Count > MaxChangedFiles;
        var evidenceComplete = !outputTruncated && !tooManyFiles && parsed.FailureReason.Length == 0;
        var failure = parsed.FailureReason;
        if (tooManyFiles) failure = $"Changed-file count exceeded the {MaxChangedFiles} evidence bound.";
        if (outputTruncated) failure = "Source-change output exceeded the bounded capture size.";

        return new SourceChangeSnapshot(
            Scope: request.Scope,
            RepositoryRoot: repository.RepositoryRoot,
            BaseRevision: baseRevision,
            CurrentRevision: currentRevision,
            Source: evidenceComplete ? "git" : "gitIncomplete",
            EvidenceComplete: evidenceComplete,
            OutputTruncated: outputTruncated,
            Files: evidenceComplete
                ? files.Values.OrderBy(file => file.RepositoryRelativePath, StringComparer.Ordinal).ToArray()
                : Array.Empty<SourceChangedFile>(),
            FailureReason: BoundFailure(failure));
    }

    private static SourceChangeSnapshot CaptureExplicitFiles(
        RepositoryResolution repository,
        string currentRevision,
        IReadOnlyList<string> touchedFiles)
    {
        var files = new Dictionary<string, SourceChangedFile>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var startDirectory = Directory.Exists(repository.AttemptedPath)
            ? repository.AttemptedPath
            : Path.GetDirectoryName(repository.AttemptedPath) ?? repository.RepositoryRoot;

        foreach (var input in touchedFiles)
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.IsPathRooted(input)
                    ? input
                    : Path.Combine(startDirectory, input));
                var relativePath = Path.GetRelativePath(repository.RepositoryRoot, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal))
                {
                    return SourceChangeSnapshot.Unavailable(
                        SourceChangeScope.ExplicitFiles,
                        "invalidRequest",
                        $"Touched path '{input}' is outside the repository root.");
                }

                files[fullPath] = new SourceChangedFile(
                    fullPath,
                    relativePath,
                    IsNew: false,
                    HasLineEvidence: false,
                    ChangedLines: Array.Empty<SourceLineRange>());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return SourceChangeSnapshot.Unavailable(
                    SourceChangeScope.ExplicitFiles,
                    "invalidRequest",
                    BoundFailure(ex.Message));
            }
        }

        return new SourceChangeSnapshot(
            Scope: SourceChangeScope.ExplicitFiles,
            RepositoryRoot: repository.RepositoryRoot,
            BaseRevision: "",
            CurrentRevision: currentRevision,
            Source: "caller",
            EvidenceComplete: true,
            OutputTruncated: false,
            Files: files.Values.OrderBy(file => file.RepositoryRelativePath, StringComparer.Ordinal).ToArray(),
            FailureReason: "");
    }

    private static string[] BuildDiffArguments(string comparison)
        => new[]
        {
            "-c",
            "core.quotePath=false",
            "diff",
            "--no-color",
            "--no-ext-diff",
            "--find-renames",
            "--unified=0",
            comparison,
            "--",
        };

    private static DiffParseResult ParseDiff(string repositoryRoot, string output)
    {
        var files = new List<MutableChangedFile>();
        MutableChangedFile? current = null;
        var nextFileIsNew = false;
        var failure = "";

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = null;
                nextFileIsNew = false;
                continue;
            }

            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                nextFileIsNew = true;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var rawPath = line[4..];
                if (rawPath == "/dev/null")
                {
                    current = null;
                    continue;
                }

                if (!TryDecodeGitPatchPath(rawPath, out rawPath))
                {
                    failure = "Git returned an unrecognized quoted path.";
                    current = null;
                    continue;
                }
                if (rawPath.StartsWith("b/", StringComparison.Ordinal)) rawPath = rawPath[2..];
                if (!TryCanonicalizeRepositoryPath(
                        repositoryRoot,
                        rawPath,
                        out var fullPath,
                        out var relativePath))
                {
                    failure = "Git returned an invalid changed path.";
                    current = null;
                    continue;
                }

                current = new MutableChangedFile(fullPath, relativePath, nextFileIsNew);
                files.Add(current);
                continue;
            }

            if (current == null || !line.StartsWith("@@ ", StringComparison.Ordinal)) continue;
            var match = DiffHunk().Match(line);
            if (!match.Success)
            {
                failure = "Git returned an unrecognized diff hunk.";
                continue;
            }

            var startLine = int.Parse(match.Groups["start"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var count = match.Groups["count"].Success
                ? int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1;
            if (count > 0)
            {
                current.ChangedLines.Add(new SourceLineRange(startLine, checked(startLine + count - 1)));
            }
        }

        return new DiffParseResult(
            files.Select(file => new SourceChangedFile(
                    file.FilePath,
                    file.RepositoryRelativePath,
                    file.IsNew,
                    HasLineEvidence: true,
                    ChangedLines: file.ChangedLines.ToArray()))
                .ToArray(),
            failure);
    }

    private static bool TryDecodeGitPatchPath(string input, out string decoded)
    {
        decoded = input;
        if (input.Length < 2 || input[0] != '"') return true;
        if (input[^1] != '"') return false;

        var result = new StringBuilder(input.Length - 2);
        for (var index = 1; index < input.Length - 1; index++)
        {
            var current = input[index];
            if (current != '\\')
            {
                result.Append(current);
                continue;
            }

            if (++index >= input.Length - 1) return false;
            current = input[index];
            switch (current)
            {
                case '\\': result.Append('\\'); break;
                case '"': result.Append('"'); break;
                case 't': result.Append('\t'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'v': result.Append('\v'); break;
                default:
                    if (current is < '0' or > '7') return false;
                    var value = current - '0';
                    for (var digit = 0; digit < 2 && index + 1 < input.Length - 1; digit++)
                    {
                        var next = input[index + 1];
                        if (next is < '0' or > '7') break;
                        index++;
                        value = (value * 8) + (next - '0');
                    }
                    result.Append((char)value);
                    break;
            }
        }

        decoded = result.ToString();
        return true;
    }

    private static bool TryCanonicalizeRepositoryPath(
        string repositoryRoot,
        string gitPath,
        out string fullPath,
        out string relativePath)
    {
        fullPath = "";
        relativePath = "";
        try
        {
            var normalized = gitPath.Replace('/', Path.DirectorySeparatorChar);
            fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, normalized));
            relativePath = Path.GetRelativePath(repositoryRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return relativePath != ".." && !relativePath.StartsWith("../", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static RepositoryResolution ResolveRepository(string? startPath)
    {
        var start = NormalizeStartPath(startPath);
        if (start == null)
        {
            return RepositoryResolution.Failed(
                startPath ?? "",
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
            return RepositoryResolution.Failed(
                start.AttemptedPath,
                source,
                BoundFailure(rootResult.Error));
        }

        var repositoryRoot = Path.GetFullPath(
            rootResult.Output.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        return repositoryRoot.Length == 0 || !Directory.Exists(repositoryRoot)
            ? RepositoryResolution.Failed(
                start.AttemptedPath,
                "gitFailed",
                "Git returned an invalid repository root.")
            : new RepositoryResolution(true, start.AttemptedPath, repositoryRoot, "git", "");
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
        => RunWithLimit(workingDirectory, MaxOutputChars, arguments);

    private static GitCommandResult RunWithLimit(
        string workingDirectory,
        int maxOutputChars,
        params string[] arguments)
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
            var outputTask = StartBoundedDrain(process.StandardOutput, maxOutputChars);
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

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,(?<count>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex DiffHunk();

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

    private sealed record RepositoryResolution(
        bool Success,
        string AttemptedPath,
        string RepositoryRoot,
        string Source,
        string FailureReason)
    {
        public static RepositoryResolution Failed(string attemptedPath, string source, string failureReason)
            => new(false, attemptedPath, "", source, failureReason);
    }

    private sealed record DiffParseResult(SourceChangedFile[] Files, string FailureReason);

    private sealed record MutableChangedFile(
        string FilePath,
        string RepositoryRelativePath,
        bool IsNew)
    {
        public List<SourceLineRange> ChangedLines { get; } = new();
    }
}
