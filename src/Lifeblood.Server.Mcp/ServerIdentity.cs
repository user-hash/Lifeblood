using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Process-local identity and citation helpers for the MCP server.
/// Kept in the server composition layer because it touches assemblies,
/// optional git metadata, and repository-relative documentation paths.
/// </summary>
public static class ServerIdentity
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly string[] SessionLocalDoNotCiteFields =
    {
        "envelope.analysisGeneration",
        "envelope.stalenessSeconds",
        "envelope.filesChangedSinceAnalyze",
    };

    public static string ResolveServerVersion() => ResolveVersionInfo().Version;

    public static ServerVersionInfo ResolveVersionInfo()
    {
        var asm = typeof(ServerIdentity).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return new ServerVersionInfo(
                Version: informational!,
                VersionSource: "assemblyInformationalVersion",
                BuildMetadata: ExtractBuildMetadata(informational!));
        }

        var assemblyVersion = asm.GetName().Version?.ToString(3);
        if (!string.IsNullOrWhiteSpace(assemblyVersion))
        {
            return new ServerVersionInfo(
                Version: assemblyVersion!,
                VersionSource: "assemblyNameVersion",
                BuildMetadata: "");
        }

        return new ServerVersionInfo(
            Version: "0.0.0",
            VersionSource: "unknown",
            BuildMetadata: "");
    }

    public static object BuildCapabilities(ServerSessionInfo session)
    {
        var definitions = ToolRegistry.GetDefinitions();
        var readSide = definitions.Where(d => d.Availability == ToolAvailability.ReadSide).Select(d => d.Name).ToArray();
        var writeSide = definitions.Where(d => d.Availability == ToolAvailability.WriteSide).Select(d => d.Name).ToArray();
        var summarizeCapable = definitions
            .Where(d => JsonSerializer.Serialize(d.InputSchema, CompactJson).Contains("\"summarize\"", StringComparison.Ordinal))
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var repoRoot = FindRepositoryRoot();

        return new
        {
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(repoRoot),
            tools = new
            {
                totalCount = definitions.Length,
                readSideCount = readSide.Length,
                writeSideCount = writeSide.Length,
                readSide,
                writeSide,
            },
            featureFlags = new
            {
                multiProfileAnalyze = true,
                assignmentCoverage = definitions.Any(d => d.Name == "lifeblood_assignment_coverage"),
                writeSideProfileScope = true,
                evidenceReceipts = true,
                strictJsonDuplicateRejection = true,
                operationalTelemetry = true,
                operationalTelemetryEvents = new[]
                {
                    "lifeblood.tool.success_result",
                    "lifeblood.tool.error_result",
                    "lifeblood.tool.exception",
                    "lifeblood.tool.response_json",
                    "lifeblood.tool.truncated",
                    "lifeblood.tool.arguments",
                    "lifeblood.analyze.result",
                    "lifeblood.analyze.fallback",
                    "lifeblood.cache.lookup",
                },
                toolArgumentContracts = true,
                jsonCompatibilityModes = new[] { "legacy", "warn", "strict" },
                summarizeCapableTools = summarizeCapable,
            },
            contract = new
            {
                toolSchemaVersion = "v1",
                schemaSnapshotPath = BuildRepoPath(repoRoot, "schemas", "tools", "v1"),
                statusDocAnchorPath = BuildRepoPath(repoRoot, "docs", "STATUS.md"),
            },
            session = new
            {
                hasGraphLoaded = session.HasGraphLoaded,
                hasCompilationState = session.HasCompilationState,
                analysisGeneration = session.AnalysisGeneration,
                projectRoot = session.ProjectRoot,
                retainedProfileName = session.RetainedProfileName,
                retainedProfileNames = session.RetainedProfileNames,
            },
        };
    }

    public static object? BuildAnalyzeEvidenceReceipt(
        string mode,
        string? requestedMode,
        SemanticGraph? graph,
        AnalysisResult? analysis,
        string? projectPath,
        string? graphPath,
        string? rulesPath,
        string[]? activeProfiles,
        string? fallbackReason)
    {
        if (graph == null) return null;

        var repoRoot = FindRepositoryRoot();
        return new
        {
            kind = "lifeblood.analyze",
            citationSafe = true,
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(repoRoot),
            queryRecipe = new
            {
                tool = "lifeblood_analyze",
                projectPath,
                graphPath,
                rulesPath,
                mode,
                requestedMode,
                fallbackReason,
                activeProfiles = activeProfiles ?? Array.Empty<string>(),
            },
            counts = new
            {
                symbols = graph.Symbols.Count,
                edges = graph.Edges.Count,
                modules = analysis?.Metrics.TotalModules ?? 0,
                types = analysis?.Metrics.TotalTypes ?? 0,
                files = analysis?.Metrics.TotalFiles ?? 0,
                violations = analysis?.Violations.Length ?? 0,
                cycles = analysis?.Cycles.Length ?? 0,
                profileCount = activeProfiles?.Length ?? 1,
            },
            contract = new
            {
                statusDocAnchorPath = BuildRepoPath(repoRoot, "docs", "STATUS.md"),
            },
            doNotCite = SessionLocalDoNotCiteFields,
        };
    }

    public static object BuildInvariantEvidenceReceipt(
        string projectRoot,
        Lifeblood.Application.Ports.Right.Invariants.InvariantAudit audit)
    {
        var repoRoot = FindRepositoryRoot();
        return new
        {
            kind = "lifeblood.invariant_audit",
            citationSafe = true,
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(repoRoot),
            workspaceRoot = projectRoot,
            queryRecipe = new
            {
                tool = "lifeblood_invariant_check",
                mode = "audit",
            },
            invariantTotal = audit.TotalCount,
            declaredCount = audit.DeclaredCount,
            duplicateDeclarationCount = audit.DuplicateDeclarationCount,
            sourcePaths = audit.SourcePaths,
            sourceCounts = audit.SourceCounts,
            duplicateIds = audit.Duplicates.Select(d => d.Id).ToArray(),
            duplicates = audit.Duplicates,
            parseWarnings = audit.ParseWarnings,
            contract = new
            {
                statusDocAnchorPath = BuildRepoPath(repoRoot, "docs", "STATUS.md"),
            },
            doNotCite = SessionLocalDoNotCiteFields,
        };
    }

    private static object BuildServerBlock()
    {
        var version = ResolveVersionInfo();
        var asm = typeof(ServerIdentity).Assembly;
        return new
        {
            name = "lifeblood",
            version = version.Version,
            versionSource = version.VersionSource,
            buildMetadata = version.BuildMetadata,
            assemblyName = asm.GetName().Name ?? "",
            targetFramework = asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "",
        };
    }

    private static object BuildSourceControlBlock(string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return new
            {
                repositoryRoot = "",
                commitHash = "",
                shortCommitHash = "",
                dirty = (bool?)null,
                state = "unknown",
                source = "repositoryNotFound",
            };
        }

        var commit = RunGit(repoRoot, "rev-parse", "HEAD");
        var status = RunGit(repoRoot, "status", "--porcelain");
        var hasCommit = commit.Success && !string.IsNullOrWhiteSpace(commit.Output);
        var hasStatus = status.Success;
        bool? dirty = hasStatus ? status.Output.Length > 0 : null;
        return new
        {
            repositoryRoot = repoRoot,
            commitHash = hasCommit ? commit.Output.Trim() : "",
            shortCommitHash = hasCommit ? commit.Output.Trim()[..Math.Min(12, commit.Output.Trim().Length)] : "",
            dirty,
            state = dirty == true ? "dirty" : dirty == false ? "clean" : "unknown",
            source = hasCommit || hasStatus ? "git" : "unknown",
        };
    }

    private static (bool Success, string Output) RunGit(string repoRoot, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process == null) return (false, "");
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "");
            }

            var output = process.StandardOutput.ReadToEnd();
            return (process.ExitCode == 0, output.Trim());
        }
        catch
        {
            return (false, "");
        }
    }

    private static string? FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Lifeblood.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        return null;
    }

    private static string BuildRepoPath(string? repoRoot, params string[] parts)
        => string.IsNullOrWhiteSpace(repoRoot)
            ? string.Join("/", parts)
            : Path.Combine(new[] { repoRoot }.Concat(parts).ToArray());

    private static string ExtractBuildMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 && plus < version.Length - 1 ? version[(plus + 1)..] : "";
    }
}

public sealed record ServerVersionInfo(string Version, string VersionSource, string BuildMetadata);

public sealed record ServerSessionInfo(
    bool HasGraphLoaded,
    bool HasCompilationState,
    long AnalysisGeneration,
    string ProjectRoot,
    string? RetainedProfileName,
    string[] RetainedProfileNames);
