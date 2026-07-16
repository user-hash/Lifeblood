using System.Reflection;
using System.Runtime.Versioning;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Process-local identity and citation helpers for the MCP server.
/// Kept in the server composition layer because it touches assemblies and
/// repository-relative documentation paths. Source-control discovery is
/// delegated through the Application-owned port.
/// </summary>
public static class ServerIdentity
{
    public const string SharedSessionTransportMaturity = "recommended";

    private static readonly string[] SessionLocalDoNotCiteFields =
    {
        "envelope.analysisGeneration",
        "envelope.snapshotId",
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

    public static object BuildCapabilities(
        ServerSessionInfo session,
        ISourceControlSnapshotProvider sourceControl)
    {
        var definitions = ToolRegistry.GetDefinitions();
        var readSide = definitions.Where(d => d.Availability == ToolAvailability.ReadSide).Select(d => d.Name).ToArray();
        var writeSide = definitions.Where(d => d.Availability == ToolAvailability.WriteSide).Select(d => d.Name).ToArray();
        var behaviorContracts = definitions.Select(d => new
        {
            name = d.Name,
            sessionRequirement = d.Behavior.SessionRequirement.ToString(),
            effect = d.Behavior.Effect.ToString(),
            sessionAccess = d.Behavior.SessionAccess.ToString(),
            supportsSnapshotRead = d.SupportsSnapshotRead,
        }).ToArray();
        var sessionRequirementCounts = definitions
            .GroupBy(d => d.Behavior.SessionRequirement)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);
        var effectCounts = definitions
            .GroupBy(d => d.Behavior.Effect)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);
        var sessionAccessCounts = definitions
            .GroupBy(d => d.Behavior.SessionAccess)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);
        var summarizeCapable = definitions
            .Where(HasBooleanSummarizeArgument)
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var repoRoot = FindServerRepositoryRoot();

        return new
        {
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(sourceControl.Capture(repoRoot)),
            tools = new
            {
                totalCount = definitions.Length,
                readSideCount = readSide.Length,
                writeSideCount = writeSide.Length,
                readSide,
                writeSide,
                compatibilityNote = "readSide/writeSide are legacy projections of sessionRequirement; use behaviorContracts for policy.",
                sessionRequirementCounts,
                effectCounts,
                sessionAccessCounts,
                behaviorContracts,
            },
            featureFlags = new
            {
                multiProfileAnalyze = true,
                assignmentCoverage = definitions.Any(d => d.Name == "lifeblood_assignment_coverage"),
                writeSideProfileScope = true,
                evidenceReceipts = true,
                acceptedChangeReceipts = true,
                snapshotReadPreconditions = true,
                snapshotReadBatch = true,
                snapshotHistoryCatalog = true,
                historicalSnapshotSelection = true,
                historicalSnapshotsRetainSemanticServices = false,
                sharedSessionTransport = true,
                sharedSessionRequestCancellation = true,
                sharedSessionTransportActive = session.SharedService.Active,
                sharedSessionTransportMode = session.SharedService.Mode,
                sharedSessionTransportMaturity = SharedSessionTransportMaturity,
                strictJsonDuplicateRejection = true,
                operationalTelemetry = true,
                operationalTelemetryEvents = McpTelemetryEvents.All,
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
                snapshotId = session.SnapshotId,
                projectRoot = session.ProjectRoot,
                retainedProfileName = session.RetainedProfileName,
                retainedProfileNames = session.RetainedProfileNames,
                compilationStateRecoveryHint = session.CompilationStateRecoveryHint,
                snapshotHistory = new
                {
                    configuredLimit = session.SnapshotHistoryLimit,
                    hardMaximum = session.SnapshotHistoryHardMaximum,
                    maximumAgeSeconds = session.SnapshotHistoryMaximumAgeSeconds,
                    retainedCount = session.RetainedSnapshotCount,
                    pinnedCount = session.PinnedSnapshotCount,
                    droppedAutomaticRetentionCount = session.DroppedSnapshotRetentionCount,
                    retentionMode = "graph-only",
                    semanticBaseCount = session.SemanticBaseCount,
                    additionalSemanticBaseCount = 0,
                },
            },
            sharedService = new
            {
                supported = true,
                active = session.SharedService.Active,
                mode = session.SharedService.Mode,
                protocolVersion = session.SharedService.ProtocolVersion,
                daemonInstanceId = session.SharedService.DaemonInstanceId,
                buildIdentity = session.SharedService.BuildIdentity,
                processId = session.SharedService.ProcessId,
                processStartedAtUtc = session.SharedService.ProcessStartedAtUtc,
                workspaceRoot = session.SharedService.WorkspaceRoot,
                lifecycleState = session.SharedService.LifecycleState,
                clientCount = session.SharedService.ClientCount,
                activeRequestCount = session.SharedService.ActiveRequestCount,
                inFlightAnalysisCount = session.SharedService.InFlightAnalysisCount,
                lastActivityUtc = session.SharedService.LastActivityUtc,
                idleTimeoutSeconds = session.SharedService.IdleTimeoutSeconds,
                idleDeadlineUtc = session.SharedService.IdleDeadlineUtc,
                workingSetBytes = session.SharedService.WorkingSetBytes,
                privateMemoryBytes = session.SharedService.PrivateMemoryBytes,
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
        string? fallbackReason,
        SourceControlSnapshot sourceControl)
    {
        if (graph == null) return null;

        var serverRepoRoot = FindServerRepositoryRoot();
        return new
        {
            kind = "lifeblood.analyze",
            citationSafe = true,
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(sourceControl),
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
                statusDocAnchorPath = BuildRepoPath(serverRepoRoot, "docs", "STATUS.md"),
            },
            doNotCite = SessionLocalDoNotCiteFields,
        };
    }

    /// <summary>
    /// Captures analyze provenance before compiler work begins. The analyzed
    /// project or graph owns lookup precedence; server-build fallback applies
    /// only when neither caller path exists.
    /// </summary>
    public static SourceControlSnapshot CaptureAnalyzeSourceControl(
        ISourceControlSnapshotProvider sourceControl,
        string? projectPath,
        string? graphPath)
        => sourceControl.Capture(FirstPopulated(
            projectPath,
            graphPath,
            FindServerRepositoryRoot()));

    public static object BuildInvariantEvidenceReceipt(
        string projectRoot,
        Lifeblood.Application.Ports.Right.Invariants.InvariantAudit audit,
        ISourceControlSnapshotProvider sourceControl)
    {
        var serverRepoRoot = FindServerRepositoryRoot();
        return new
        {
            kind = "lifeblood.invariant_audit",
            citationSafe = true,
            server = BuildServerBlock(),
            sourceControl = BuildSourceControlBlock(sourceControl.Capture(projectRoot)),
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
            coverage = audit.Coverage,
            coverageWarnings = audit.CoverageWarnings,
            duplicateIds = audit.Duplicates.Select(d => d.Id).ToArray(),
            duplicates = audit.Duplicates,
            parseWarnings = audit.ParseWarnings,
            contract = new
            {
                statusDocAnchorPath = BuildRepoPath(serverRepoRoot, "docs", "STATUS.md"),
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

    private static object BuildSourceControlBlock(SourceControlSnapshot snapshot)
        => new
        {
            attemptedPath = snapshot.AttemptedPath,
            repositoryRoot = snapshot.RepositoryRoot,
            commitHash = snapshot.CommitHash,
            shortCommitHash = snapshot.ShortCommitHash,
            dirty = snapshot.Dirty,
            state = snapshot.State,
            source = snapshot.Source,
            latestSemanticVersionTag = snapshot.LatestSemanticVersionTag,
            dirtyEntryCount = snapshot.DirtyEntryCount,
            dirtyEntryCountCapped = snapshot.DirtyEntryCountCapped,
            dirtyEntries = snapshot.DirtyEntries,
            dirtyEntriesTruncated = snapshot.DirtyEntriesTruncated,
            failureReason = snapshot.FailureReason,
        };

    private static string? FindServerRepositoryRoot()
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

    private static string? FirstPopulated(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static string BuildRepoPath(string? repoRoot, params string[] parts)
        => string.IsNullOrWhiteSpace(repoRoot)
            ? string.Join("/", parts)
            : Path.Combine(new[] { repoRoot }.Concat(parts).ToArray());

    private static bool HasBooleanSummarizeArgument(ToolDefinition definition)
    {
        return definition.InputContract.Arguments.TryGetValue("summarize", out var argument)
            && argument.Type == ToolArgumentType.Boolean;
    }

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
    string SnapshotId,
    string ProjectRoot,
    string? RetainedProfileName,
    string[] RetainedProfileNames,
    string? CompilationStateRecoveryHint,
    int SnapshotHistoryLimit,
    int SnapshotHistoryHardMaximum,
    double SnapshotHistoryMaximumAgeSeconds,
    int RetainedSnapshotCount,
    int PinnedSnapshotCount,
    long DroppedSnapshotRetentionCount,
    int SemanticBaseCount,
    ServerSharedServiceInfo SharedService);

public sealed record ServerSharedServiceInfo(
    bool Active,
    string Mode,
    int? ProtocolVersion,
    string DaemonInstanceId,
    string BuildIdentity,
    int ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    string WorkspaceRoot,
    string LifecycleState,
    int ClientCount,
    int ActiveRequestCount,
    int InFlightAnalysisCount,
    DateTimeOffset? LastActivityUtc,
    double? IdleTimeoutSeconds,
    DateTimeOffset? IdleDeadlineUtc,
    long WorkingSetBytes,
    long PrivateMemoryBytes);
