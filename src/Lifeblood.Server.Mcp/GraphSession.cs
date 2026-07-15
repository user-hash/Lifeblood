using System.Security.Cryptography;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-specific session wrapper. Owns the load orchestration and publishes one
/// immutable committed state after graph, rules, and semantic ports succeed.
/// </summary>
public sealed class GraphSession : IDisposable
{
    private static readonly IUsageProbe UsageProbe = new ProcessUsageProbe();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IFileSystem _fs;
    private readonly ITelemetrySink _telemetry;
    private readonly ISourceControlSnapshotProvider _sourceControl;
    private readonly AnalysisRuleSetResolver _ruleSetResolver;
    private readonly WorkspaceSnapshotCatalog _snapshotCatalog;
    private readonly object _publicationSync = new();
    private readonly AsyncLocal<CommittedGraphSessionState?> _leasedState = new();
    private CommittedGraphSessionState _current = CommittedGraphSessionState.CreateEmpty();

    private CommittedGraphSessionState Current =>
        _leasedState.Value ?? Volatile.Read(ref _current);

    public GraphSession(
        IFileSystem fs,
        ITelemetrySink? telemetry = null,
        WorkspaceSnapshotCatalog? snapshotCatalog = null,
        ISourceControlSnapshotProvider? sourceControl = null)
    {
        _fs = fs;
        _telemetry = telemetry ?? NoOpTelemetrySink.Instance;
        _sourceControl = sourceControl ?? UnavailableSourceControlSnapshotProvider.Instance;
        _ruleSetResolver = new AnalysisRuleSetResolver(fs);
        _snapshotCatalog = snapshotCatalog ?? new WorkspaceSnapshotCatalog();
    }

    /// <summary>
    /// Project root of the most recent Roslyn <c>lifeblood_analyze</c>
    /// call. Empty when the session was loaded from a JSON graph or no
    /// project was loaded at all. Exposed so the MCP tool layer can
    /// resolve relative file paths for features like
    /// <c>lifeblood_partial_view</c>, which reads source off disk.
    /// </summary>
    public string ProjectRoot => Current.Workspace.Context?.RootPath ?? "";

    /// <summary>
    /// Explicit context for providers that resolve graph-relative paths on
    /// disk. Null for JSON imports and empty sessions; never inferred from the
    /// server process current directory.
    /// </summary>
    public WorkspaceContext? CurrentWorkspaceContext => Current.Workspace.Context;

    /// <summary>Exposed file-system port for tool handlers that need disk access (partial view, compile_check auto-refresh).</summary>
    public IFileSystem FileSystem => _fs;

    /// <summary>Source-control evidence authority composed for this host.</summary>
    public ISourceControlSnapshotProvider SourceControl => _sourceControl;

    /// <summary>
    /// UTC moment the most recent analyze (full or incremental) finished.
    /// Null when no graph is loaded. Read by the response decorator
    /// (LB-INBOX-001 / LB-OBS-004) to compute staleness on every read-side
    /// tool response. JSON-graph imports set this to the import time so a
    /// caller still sees a non-zero (but small) staleness signal.
    /// </summary>
    public DateTime? AnalyzedAtUtc => Current.Workspace.AnalyzedAtUtc;

    /// <summary>
    /// True when the project root looks like a Unity workspace —
    /// presence of a top-level Library/ directory is the cheapest
    /// reliable signal Unity has touched the project. Used to decide
    /// whether to inject the Unity assembly resolver into the code
    /// executor. INV-EXECUTE-001.
    /// </summary>
    private bool LooksLikeUnityWorkspace(string? projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return false;
        return _fs.DirectoryExists(System.IO.Path.Combine(projectRoot, "Library"));
    }

    private (ICompilationHost? Host, ICodeExecutor? Executor, IWorkspaceRefactoring? Refactoring)
        CreateCompilationServices(
            RoslynWorkspaceAnalyzer adapter,
            SemanticGraph graph,
            string? projectRoot)
    {
        if (adapter.Compilations is not { Count: > 0 } compilations)
            return (null, null, null);

        // INV-VIEW-002: build the typed semantic view once and share it with
        // every service created for this candidate. Construction happens only
        // after graph validation and rule analysis have succeeded.
        RoslynCompilationHost? compilationHost = null;
        RoslynWorkspaceRefactoring? refactoring = null;
        try
        {
            var view = new RoslynSemanticView(
                compilations,
                graph,
                adapter.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));
            compilationHost = new RoslynCompilationHost(compilations, adapter.ModuleDependencies);
            var unityResolver = LooksLikeUnityWorkspace(projectRoot)
                ? new UnityAssemblyResolver(_fs, projectRoot!)
                : null;
            var codeExecutor = new RoslynCodeExecutor(view, unityResolver);
            refactoring = new RoslynWorkspaceRefactoring(compilations, adapter.ModuleDependencies);
            return (compilationHost, codeExecutor, refactoring);
        }
        catch
        {
            compilationHost?.Dispose();
            refactoring?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Capture one coherent Application snapshot. Callers that need several
    /// fields from the same generation should read through this value.
    /// </summary>
    public WorkspaceSnapshot CurrentSnapshot => Current.Workspace;

    /// <summary>
    /// Current Unity package source visibility receipt, if the loaded
    /// workspace has package descriptors. This stays with the live Roslyn
    /// adapter and is intentionally absent from graph-only historical
    /// selections.
    /// </summary>
    public PackageSourceVisibilityReport? PackageSourceVisibility =>
        Current.RoslynAdapter?.PackageSourceVisibility;

    public PackageSourceVisibilityFile? ResolvePackageSource(string filePath) =>
        Current.RoslynAdapter?.ResolvePackageSource(filePath);

    /// <summary>
    /// Latest committed publication, independent of any request-scoped
    /// historical selection. Process-level inventory and memory facts use this
    /// view; tool result data continues to use <see cref="CurrentSnapshot"/>.
    /// </summary>
    public WorkspaceSnapshot LatestSnapshot => Volatile.Read(ref _current).Workspace;

    /// <summary>
    /// True only inside an exact graph-only historical lease. Consumers that
    /// require live source files or semantic services must not present those
    /// changing resources as evidence from the historical publication.
    /// </summary>
    public bool IsHistoricalSelection => Current.IsHistorical;

    public WorkspaceSnapshotCatalogOptions SnapshotCatalogOptions => _snapshotCatalog.Options;

    public long DroppedSnapshotRetentionCount => _snapshotCatalog.DroppedAutomaticRetentionCount;

    public IReadOnlyList<WorkspaceSnapshotCatalogEntry> SnapshotHistory => _snapshotCatalog.List();

    /// <summary>
    /// Pin one complete host/Application generation for the current execution
    /// context. Publication may continue concurrently; all session properties
    /// resolve through the leased state until the scope is disposed.
    /// </summary>
    public IDisposable AcquireReadLease()
    {
        if (_leasedState.Value != null)
            return NestedReadLease.Instance;

        while (true)
        {
            var state = Volatile.Read(ref _current);
            if (!state.Workspace.TryAcquireLease(out var snapshotLease))
            {
                Thread.Yield();
                continue;
            }

            _leasedState.Value = state;
            return new GraphSessionReadLease(this, state, snapshotLease);
        }
    }

    /// <summary>
    /// Lease one exact current or graph-only historical publication. Explicit
    /// selection is synchronized only with the atomic publication seam; it
    /// never blocks while a candidate is being built.
    /// </summary>
    public IDisposable AcquireReadLease(SnapshotId selectedSnapshotId)
    {
        ArgumentNullException.ThrowIfNull(selectedSnapshotId);
        if (selectedSnapshotId.IsNone)
            throw new ArgumentException("A selected snapshot id cannot be empty.", nameof(selectedSnapshotId));

        var nested = _leasedState.Value;
        if (nested != null)
        {
            if (nested.Workspace.SnapshotId != selectedSnapshotId)
            {
                throw new WorkspaceSnapshotSelectionConflictException(
                    selectedSnapshotId,
                    nested.Workspace.SnapshotId);
            }

            return NestedReadLease.Instance;
        }

        lock (_publicationSync)
        {
            var current = Volatile.Read(ref _current);
            WorkspaceSnapshotLease snapshotLease;
            CommittedGraphSessionState selectedState;
            if (current.Workspace.SnapshotId == selectedSnapshotId)
            {
                snapshotLease = current.Workspace.AcquireLease();
                selectedState = current;
            }
            else if (_snapshotCatalog.TryAcquire(selectedSnapshotId, out snapshotLease))
            {
                selectedState = CommittedGraphSessionState.CreateHistorical(snapshotLease.Snapshot);
            }
            else
            {
                throw new WorkspaceSnapshotNotFoundException(
                    selectedSnapshotId,
                    current.Workspace.SnapshotId,
                    _snapshotCatalog.List().Select(entry => entry.Snapshot.SnapshotId).ToArray());
            }

            _leasedState.Value = selectedState;
            return new GraphSessionReadLease(this, selectedState, snapshotLease);
        }
    }

    public WorkspaceSnapshotCatalogMutation PinSnapshot(SnapshotId snapshotId, string? name)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        lock (_publicationSync)
        {
            var current = Volatile.Read(ref _current).Workspace;
            return current.SnapshotId == snapshotId
                ? _snapshotCatalog.Pin(current, name)
                : _snapshotCatalog.Pin(snapshotId, name);
        }
    }

    public WorkspaceSnapshotCatalogMutation UnpinSnapshot(SnapshotId snapshotId)
        => _snapshotCatalog.Unpin(snapshotId);

    public WorkspaceSnapshotCatalogMutation EvictSnapshot(SnapshotId snapshotId)
        => _snapshotCatalog.Evict(snapshotId);

    /// <summary>
    /// Recapture live workspace inputs once per historical base key. The C#
    /// adapter remains the source fingerprint authority; this host only
    /// compares its receipt with committed identity.
    /// </summary>
    public IReadOnlyDictionary<SnapshotId, WorkspaceSnapshotDriftStatus> CheckSnapshotDrift(
        IEnumerable<WorkspaceSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var result = new Dictionary<SnapshotId, WorkspaceSnapshotDriftStatus>();
        var currentByBase = new Dictionary<string, WorkspaceAnalysisIdentity>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Identity is not { } identity
                || snapshot.Context is not { } context
                || identity.Spec.DescriptorPolicy != AnalysisDescriptorPolicy.WorkspaceDiscovery)
            {
                result[snapshot.SnapshotId] = new WorkspaceSnapshotDriftStatus(
                    "unavailable",
                    LiveInputChecked: false,
                    Detail: "Live drift checks require a workspace-discovery publication with an explicit root.");
                continue;
            }

            try
            {
                if (!currentByBase.TryGetValue(identity.BaseKey.Value, out var currentIdentity))
                {
                    IWorkspaceInputFingerprintProvider fingerprintProvider =
                        new RoslynWorkspaceAnalyzer(_fs, new UnityDefineProfileResolver(_fs));
                    var inputs = fingerprintProvider.CaptureAnalysisInputs(
                        context.RootPath,
                        new AnalysisConfig
                        {
                            RetainCompilations = false,
                            DefineProfiles = identity.Spec.DefineProfiles.ToArray(),
                            ExcludePathGlobs = identity.Spec.ExcludePathGlobs.ToArray(),
                        });
                    var rules = _ruleSetResolver.Resolve(identity.Spec.RuleSet.Source, context.RootPath);
                    var spec = new AnalysisSpec(
                        inputs.EffectiveDefineProfiles,
                        identity.Spec.ExcludePathGlobs,
                        identity.Spec.RetentionMode,
                        identity.Spec.DescriptorPolicy,
                        rules.Identity);
                    currentIdentity = new WorkspaceAnalysisIdentity(
                        WorkspacePathIdentity.CreateWorkspaceKey(context.RootPath),
                        spec,
                        inputs.SourceFingerprint);
                    currentByBase.Add(identity.BaseKey.Value, currentIdentity);
                }

                result[snapshot.SnapshotId] = new WorkspaceSnapshotDriftStatus(
                    currentIdentity.AnalysisKey == identity.AnalysisKey ? "current" : "drifted",
                    LiveInputChecked: true,
                    CurrentAnalysisKey: currentIdentity.AnalysisKey.Value,
                    CurrentSourceFingerprint: currentIdentity.Source.Fingerprint.Value);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException)
            {
                result[snapshot.SnapshotId] = new WorkspaceSnapshotDriftStatus(
                    "unavailable",
                    LiveInputChecked: false,
                    Detail: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return result;
    }

    public SemanticGraph? Graph => Current.Workspace.Graph;
    public AnalysisResult? Analysis => Current.Workspace.Analysis;
    public Domain.Capabilities.AdapterCapability? AdapterCapability => Current.Workspace.Capability;
    public bool IsLoaded => Current.Workspace.IsLoaded;
    public ICompilationHost? CompilationHost => Current.Workspace.CompilationHost;
    public ICodeExecutor? CodeExecutor => Current.Workspace.CodeExecutor;
    public IWorkspaceRefactoring? Refactoring => Current.Workspace.Refactoring;
    public bool HasCompilationState => Current.Workspace.HasCompilationState;
    public string? CompilationStateRecoveryHint => BuildCompilationStateRecoveryHint();

    /// <summary>INV-MULTI-DEFINE-IOP-001. Name of the retained profile.</summary>
    public string? RetainedProfileName => Current.RoslynAdapter?.RetainedProfileName;

    /// <summary>
    /// INV-MULTI-DEFINE-IOP-001 / INV-MULTI-DEFINE-WRITESIDE-001. Active
    /// profile names from the most-recent analyze. Count >= 2 means the graph
    /// was built under multiple profiles; the live Roslyn write-side tools
    /// (find_references / find_definition / find_implementations / rename)
    /// still operate against the retained (first) profile's compilations only.
    /// Empty when no profile-aware analyze has run.
    /// </summary>
    public IReadOnlyList<string> RetainedProfileNames =>
        Current.RoslynAdapter?.RetainedProfileNames ?? System.Array.Empty<string>();

    /// <summary>
    /// Monotonic workspace generation. Bumped on every Load / incremental
    /// refresh / auto-refresh. Read by the envelope decorator so every
    /// read-side response carries the generation that produced it.
    /// INV-DIAGNOSE-FRESHNESS-001.
    /// </summary>
    public long AnalysisGeneration => Current.Workspace.AnalysisGeneration;

    /// <summary>
    /// Opaque identity of the exact committed publication. Unlike generation,
    /// this cannot collide across workspace daemons or process restarts.
    /// </summary>
    public SnapshotId SnapshotId => Current.Workspace.SnapshotId;

    /// <summary>The canonical equality/provenance contract of the current publication.</summary>
    public WorkspaceAnalysisIdentity? AnalysisIdentity => Current.Workspace.Identity;

    /// <summary>True if the session has a previous Roslyn analysis that supports incremental update.</summary>
    public bool CanIncremental => Current.RoslynAdapter?.HasSnapshot == true;

    internal PreparedAnalyzeRequest PrepareAnalysis(AnalyzeToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        WorkspaceAnalysisIdentity identity;
        AnalysisRuleSetResolver.ResolvedRuleSet ruleSet;
        string? projectPath = null;
        string? graphPath = null;

        if (!string.IsNullOrWhiteSpace(request.GraphPath))
        {
            graphPath = Path.GetFullPath(request.GraphPath);
            if (!_fs.FileExists(graphPath))
                throw new FileNotFoundException($"Graph file not found: {graphPath}", graphPath);

            var workspaceRoot = WorkspacePathIdentity.ResolveWorkspaceRoot(Path.GetDirectoryName(graphPath)!);
            ruleSet = _ruleSetResolver.Resolve(request.RulesPath, workspaceRoot);
            using var stream = _fs.OpenRead(graphPath);
            identity = BuildImportedGraphIdentity(
                graphPath,
                ContentFingerprint.FromHashBytes(SHA256.HashData(stream)),
                ruleSet.Identity);
        }
        else if (!string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            projectPath = Path.GetFullPath(request.ProjectPath);
            if (!_fs.DirectoryExists(projectPath))
                throw new DirectoryNotFoundException($"Project directory not found: {projectPath}");

            var committed = Current;
            var sameIncrementalWorkspace = request.Incremental
                && committed.RoslynAdapter?.HasSnapshot == true
                && WorkspacePathIdentity.Equal(committed.Workspace.Context?.RootPath, projectPath);
            ruleSet = request.RulesPath == null && sameIncrementalWorkspace
                ? RefreshCommittedRuleSet(committed, projectPath)
                : _ruleSetResolver.Resolve(request.RulesPath, projectPath);
            var excludePaths = request.ExcludePaths == null && sameIncrementalWorkspace
                ? committed.ExcludePaths
                : NormalizePathGlobs(request.ExcludePaths);
            var defineProfiles = request.DefineProfiles == null && sameIncrementalWorkspace
                ? committed.RoslynAdapter!.RetainedProfileNames.ToArray()
                : request.DefineProfiles;
            var profileScopeChanged = sameIncrementalWorkspace
                && defineProfiles is { Length: > 0 }
                && !defineProfiles.SequenceEqual(
                    committed.RoslynAdapter!.RetainedProfileNames,
                    StringComparer.Ordinal);
            var retainCompilations = sameIncrementalWorkspace
                && !(profileScopeChanged && request.AllowFullFallback)
                    ? true
                    : !request.ReadOnly;
            var config = new AnalysisConfig
            {
                RetainCompilations = retainCompilations,
                DefineProfiles = defineProfiles,
                ExcludePathGlobs = excludePaths,
                AuthoritativeChangedFiles = request.AuthoritativeChangedFiles,
                AllowFullFallback = request.AllowFullFallback,
            };
            IWorkspaceInputFingerprintProvider fingerprintProvider =
                new RoslynWorkspaceAnalyzer(_fs, new UnityDefineProfileResolver(_fs));
            var inputs = fingerprintProvider.CaptureAnalysisInputs(projectPath, config);
            var spec = new AnalysisSpec(
                inputs.EffectiveDefineProfiles,
                excludePaths,
                retainCompilations
                    ? AnalysisRetentionMode.RetainedSemantic
                    : AnalysisRetentionMode.GraphOnly,
                AnalysisDescriptorPolicy.WorkspaceDiscovery,
                ruleSet.Identity);
            identity = new WorkspaceAnalysisIdentity(
                WorkspacePathIdentity.CreateWorkspaceKey(projectPath),
                spec,
                inputs.SourceFingerprint);
        }
        else
        {
            throw new ArgumentException("Specify projectPath or graphPath.", nameof(request));
        }

        var preparedRequest = request with
        {
            ProjectPath = projectPath,
            GraphPath = graphPath,
        };
        var executionPolicy = BuildExecutionPolicyFingerprint(
            preparedRequest,
            projectPath,
            graphPath,
            ruleSet.Source);
        return new PreparedAnalyzeRequest(
            preparedRequest,
            identity,
            new AnalysisCoalescingKey(identity.AnalysisKey, executionPolicy));
    }

    /// <summary>
    /// Refresh the session if any tracked file has changed on disk since
    /// the last analyze. Idempotent: returns <c>null</c> when nothing
    /// changed, otherwise the number of files that were re-analyzed.
    /// Fails silently on non-Roslyn sessions (JSON graph imports) since
    /// those have no source on disk to diff against. Keeps
    /// <c>lifeblood_compile_check</c> from running against a stale
    /// workspace after the user edits source between the initial
    /// <c>lifeblood_analyze</c> and the next compile_check.
    /// </summary>
    public int? MaybeRefreshIfStale()
    {
        var committed = Current;
        var adapter = committed.RoslynAdapter;
        var projectPath = committed.Workspace.Context?.RootPath;
        if (adapter == null || !adapter.HasSnapshot || string.IsNullOrEmpty(projectPath))
            return null;
        try
        {
            // Auto-refresh contract: "make state fresh." This is an internal
            // best-effort caller — if the adapter cannot honor incremental
            // cleanly, the right move is to widen to a full re-analyze
            // rather than leave compile_check running on a stale graph.
            // Opt into AllowFullFallback=true here. INV-ANALYZE-FALLBACK-001.
            var config = new AnalysisConfig
            {
                RetainCompilations = true,
                AllowFullFallback = true,
                ExcludePathGlobs = committed.ExcludePaths,
            };
            var candidateAdapter = adapter.ForkForIncrementalCandidate();
            IncrementalAnalyzeResult incremental;
            using (TelemetryPhase("auto-refresh.incremental"))
            {
                incremental = candidateAdapter.IncrementalAnalyze(config);
            }
            // Mode==Rejected only happens here when the snapshot disappeared
            // mid-run (CanIncremental gated above) — degrade silently per
            // the best-effort contract.
            if (incremental.Mode == IncrementalMode.Rejected || incremental.Graph == null)
                return null;
            var graph = incremental.Graph;
            var changedFileCount = incremental.ChangedFileCount;
            var ruleSet = RefreshCommittedRuleSet(committed, projectPath);
            var identity = BuildRoslynIdentity(
                projectPath,
                candidateAdapter,
                config.ExcludePathGlobs,
                retainCompilations: true,
                ruleSet.Identity);
            if (changedFileCount == 0
                && committed.Workspace.Identity?.AnalysisKey == identity.AnalysisKey)
            {
                Commit(committed.WithRoslynAdapter(candidateAdapter));
                return null;
            }

            // Source changed — rebuild the session view. This mirrors the
            // LoadIncremental happy path but skips the response-building.
            AnalysisResult analysis;
            using (TelemetryPhase("auto-refresh.analyze"))
            {
                analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, ruleSet.Rules);
            }

            using (TelemetryPhase("auto-refresh.commit"))
            {
                WorkspaceSnapshot workspace;
                if (changedFileCount == 0)
                {
                    workspace = committed.Workspace.DeriveAnalysis(
                        analysis,
                        identity,
                        DateTime.UtcNow,
                        checked(committed.Workspace.AnalysisGeneration + 1));
                }
                else
                {
                    var (newCompilationHost, newCodeExecutor, newRefactoring) =
                        CreateCompilationServices(candidateAdapter, graph, projectPath);
                    workspace = WorkspaceSnapshot.Create(
                        graph,
                        analysis,
                        candidateAdapter.Capability,
                        "csharp",
                        committed.Workspace.Context,
                        DateTime.UtcNow,
                        checked(committed.Workspace.AnalysisGeneration + 1),
                        newCompilationHost,
                        newCodeExecutor,
                        newRefactoring,
                        identity: identity);
                }
                Commit(new CommittedGraphSessionState(
                    workspace,
                    candidateAdapter,
                    ruleSet,
                    committed.ExcludePaths));
            }

            return changedFileCount;
        }
        catch
        {
            // Auto-refresh is best-effort. A failure here should not break
            // the tool call the user actually asked for — return null and
            // let compile_check run against whatever state we have.
            return null;
        }
    }

    public string Load(string? projectPath, string? graphPath, string? rulesPath,
                       bool incremental = false, bool readOnly = false,
                       bool allowFullFallback = false,
                       string[]? defineProfiles = null,
                       string[]? excludePaths = null,
                       string[]? authoritativeChangedFiles = null,
                       AcceptedChangeReceiptRequest? acceptedChangeReceipt = null,
                       AnalysisKey? expectedAnalysisKey = null,
                       CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceControlSnapshot = ServerIdentity.CaptureAnalyzeSourceControl(
            _sourceControl,
            projectPath,
            graphPath);
        var changeReceipt = acceptedChangeReceipt ?? AcceptedChangeReceiptRequest.Summary;
        var committed = Current;
        var fullRequestedMode = "full";
        FallbackReason? fullFallbackReason = null;
        string? fullFallbackDetail = null;

        // Incremental path: reuse existing adapter, only recompile changed modules.
        // INV-ANALYZE-FALLBACK-001: caller's allowFullFallback flag flows through
        // to the adapter's policy gate. Default false = fail-loud (Rejected),
        // true = silent widening (FullFallback). Either path returns a typed
        // result the wire shape surfaces as fallbackReason / canRetryFull /
        // suggestedRetry on the response.
        if (incremental && !string.IsNullOrEmpty(projectPath))
        {
            var committedAdapter = committed.RoslynAdapter;
            var committedProjectPath = committed.Workspace.Context?.RootPath;
            var canIncrementalThisProject = committedAdapter?.HasSnapshot == true
                && string.Equals(projectPath, committedProjectPath, StringComparison.OrdinalIgnoreCase);

            if (canIncrementalThisProject)
            {
                var requestedProfiles = defineProfiles?
                    .Where(profile => !string.IsNullOrWhiteSpace(profile))
                    .Select(profile => profile.Trim())
                    .ToArray();
                var profileScopeChanged = requestedProfiles is { Length: > 0 }
                    && !requestedProfiles.SequenceEqual(
                        committedAdapter!.RetainedProfileNames,
                        StringComparer.Ordinal);
                if (profileScopeChanged)
                {
                    var detail = "Define-profile order/set changed; full re-analyze required because the first profile owns retained compilations.";
                    if (!allowFullFallback)
                    {
                        return BuildLoadResult(
                            mode: "rejected",
                            graph: null,
                            analysis: null,
                            usage: null,
                            sourceControl: sourceControlSnapshot,
                            acceptedChanges: EmptyAcceptedChanges(authoritativeChangedFiles),
                            changeReceipt: changeReceipt,
                            skipped: committedAdapter?.SkippedFiles,
                            packageSourceVisibility: committedAdapter?.PackageSourceVisibility,
                            requestedMode: "incremental",
                            fallbackReason: FallbackReason.AnalysisScopeChanged,
                            fallbackDetail: detail,
                            canRetryFull: true,
                            projectPath: projectPath,
                            rulesPath: rulesPath,
                            identity: committed.Workspace.Identity);
                    }

                    fullRequestedMode = "incremental";
                    fullFallbackReason = FallbackReason.AnalysisScopeChanged;
                    fullFallbackDetail = detail;
                }
                else if (!readOnly && !committed.Workspace.HasCompilationState)
                {
                    var detail = BuildCompilationStateRecoveryDetail(projectPath);
                    if (!allowFullFallback)
                    {
                        return BuildLoadResult(
                            mode: "rejected",
                            graph: null,
                            analysis: null,
                            usage: null,
                            sourceControl: sourceControlSnapshot,
                            acceptedChanges: EmptyAcceptedChanges(authoritativeChangedFiles),
                            changeReceipt: changeReceipt,
                            skipped: committedAdapter?.SkippedFiles,
                            packageSourceVisibility: committedAdapter?.PackageSourceVisibility,
                            requestedMode: "incremental",
                            fallbackReason: FallbackReason.CompilationStateUnavailable,
                            fallbackDetail: detail,
                            canRetryFull: true,
                            projectPath: projectPath,
                            rulesPath: rulesPath);
                    }

                    fullRequestedMode = "incremental";
                    fullFallbackReason = FallbackReason.CompilationStateUnavailable;
                    fullFallbackDetail = detail;
                }
                else
                {
                    return LoadIncremental(
                        committed,
                        projectPath,
                        sourceControlSnapshot,
                        rulesPath,
                        allowFullFallback,
                        excludePaths,
                        authoritativeChangedFiles,
                        changeReceipt,
                        expectedAnalysisKey,
                        cancellationToken);
                }
            }

            if (!canIncrementalThisProject)
            {
                // No prior snapshot (first call) OR snapshot is for a different
                // project. Both are "no prior analysis for THIS project" from the
                // caller's POV, both map to FallbackReason.NoPriorAnalysis. The
                // adapter would return the same Rejected/NoPriorAnalysis result
                // if invoked, but we don't have an adapter to invoke yet — so
                // synthesize the wire response directly. INV-ANALYZE-FALLBACK-001.
                var noPriorDetail = committedAdapter?.HasSnapshot != true
                    ? "No previous analysis snapshot. Call lifeblood_analyze first (without incremental:true)."
                    : $"Previous analysis was for a different project ('{committedProjectPath}'); current request is for '{projectPath}'.";
                if (!allowFullFallback)
                {
                    return BuildLoadResult(
                        mode: "rejected",
                        graph: null,
                        analysis: null,
                        usage: null,
                        sourceControl: sourceControlSnapshot,
                        acceptedChanges: EmptyAcceptedChanges(authoritativeChangedFiles),
                        changeReceipt: changeReceipt,
                        skipped: null,
                        requestedMode: "incremental",
                        fallbackReason: FallbackReason.NoPriorAnalysis,
                        fallbackDetail: noPriorDetail,
                        canRetryFull: true,
                        projectPath: projectPath,
                        rulesPath: rulesPath);
                }

                // Caller accepted widening, but requested intent and cause remain
                // observable on the successful full response.
                fullRequestedMode = "incremental";
                fullFallbackReason = FallbackReason.NoPriorAnalysis;
                fullFallbackDetail = noPriorDetail;
            }
        }

        SemanticGraph graph;
        Domain.Capabilities.AdapterCapability? capability = null;
        string language = "unknown";
        AnalysisUsage? usage = null;
        ICompilationHost? newCompilationHost = null;
        ICodeExecutor? newCodeExecutor = null;
        IWorkspaceRefactoring? newRefactoring = null;
        RoslynWorkspaceAnalyzer? candidateRoslynAdapter = null;
        string? candidateProjectPath = null;
        var candidateExcludePaths = Array.Empty<string>();
        AnalysisRuleSetResolver.ResolvedRuleSet? candidateRuleSet = null;
        ContentFingerprint? importedGraphContent = null;

        if (!string.IsNullOrEmpty(graphPath))
        {
            // JSON graph path: import + validate here (no use case involved)
            if (!_fs.FileExists(graphPath))
                return $"Graph file not found: {graphPath}";

            var graphWorkspaceRoot = WorkspacePathIdentity.ResolveWorkspaceRoot(
                Path.GetDirectoryName(Path.GetFullPath(graphPath))!);
            candidateRuleSet = _ruleSetResolver.Resolve(rulesPath, graphWorkspaceRoot);

            using (var fingerprintStream = _fs.OpenRead(graphPath))
                importedGraphContent = ContentFingerprint.FromHashBytes(SHA256.HashData(fingerprintStream));

            GraphDocument doc;
            using (TelemetryPhase("json-import"))
            {
                using var stream = _fs.OpenRead(graphPath);
                doc = new JsonGraphImporter().ImportDocument(stream);
            }
            graph = doc.Graph;
            language = doc.Language;
            capability = doc.Adapter ?? UnknownImportedGraphCapability(language);

            // Validate — JSON graphs don't go through AnalyzeWorkspaceUseCase
            GraphValidationError[] errors;
            using (TelemetryPhase("json-validate"))
            {
                errors = GraphValidator.Validate(graph);
            }
            if (errors.Length > 0)
                return $"Graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";

        }
        else if (!string.IsNullOrEmpty(projectPath))
        {
            // Roslyn path: AnalyzeWorkspaceUseCase validates internally
            if (!_fs.DirectoryExists(projectPath))
                return $"Project directory not found: {projectPath}";

            candidateRuleSet = _ruleSetResolver.Resolve(rulesPath, projectPath);

            // INV-MULTI-DEFINE-UNITY-RESOLVER-001. UnityDefineProfileResolver
            // auto-detects Library/ and returns 2 profiles on Unity, single
            // Editor identity on non-Unity — safe injection everywhere.
            var adapter = new RoslynWorkspaceAnalyzer(_fs, new UnityDefineProfileResolver(_fs));
            var retainCompilations = !readOnly;
            var effectiveExcludePaths = NormalizePathGlobs(excludePaths);
            var progress = new StderrProgressSink();
            adapter.OnModuleProgress = (name, i, total) =>
                Console.Error.WriteLine($"[{i}/{total}] Compiling {name}");
            AnalyzeWorkspaceResult result;
            using (TelemetryPhase("workspace-analyze",
                new TelemetryTag("analyze.read_only", readOnly),
                new TelemetryTag("analyze.profile_count", defineProfiles?.Length ?? 1)))
            {
                result = new AnalyzeWorkspaceUseCase(adapter, progress, UsageProbe)
                    .Execute(projectPath, new AnalysisConfig
                    {
                        RetainCompilations = retainCompilations,
                        DefineProfiles = defineProfiles,
                        ExcludePathGlobs = effectiveExcludePaths,
                        AuthoritativeChangedFiles = authoritativeChangedFiles,
                    });
            }
            graph = result.Graph;
            capability = adapter.Capability;
            language = "csharp";
            usage = result.Usage;

            // Candidate-only until graph validation, rule analysis, and
            // compilation-service construction have all succeeded.
            candidateRoslynAdapter = adapter;
            candidateProjectPath = projectPath;
            candidateExcludePaths = effectiveExcludePaths;
        }
        else
        {
            return "Specify projectPath or graphPath";
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Analyze (rules are optional — resolve built-in name first, then file path)
        var resolvedRuleSet = candidateRuleSet
            ?? throw new InvalidOperationException("Rule identity was not resolved for the analysis candidate.");
        AnalysisResult analysis;
        using (TelemetryPhase("rules-analyze"))
        {
            analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, resolvedRuleSet.Rules);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var identity = candidateRoslynAdapter != null
            ? BuildRoslynIdentity(
                candidateProjectPath!,
                candidateRoslynAdapter,
                candidateExcludePaths,
                retainCompilations: !readOnly,
                resolvedRuleSet.Identity)
            : BuildImportedGraphIdentity(
                graphPath!,
                importedGraphContent
                    ?? throw new InvalidOperationException("Imported graph fingerprint was not captured."),
                resolvedRuleSet.Identity);
        EnsureExpectedAnalysisKey(expectedAnalysisKey, identity);

        if (candidateRoslynAdapter != null)
        {
            (newCompilationHost, newCodeExecutor, newRefactoring) =
                CreateCompilationServices(candidateRoslynAdapter, graph, candidateProjectPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Publish the complete host + Application state through one reference.
        using (TelemetryPhase("session-commit"))
        {
            var context = string.IsNullOrEmpty(candidateProjectPath)
                ? null
                : new WorkspaceContext(candidateProjectPath);
            var workspace = WorkspaceSnapshot.Create(
                graph,
                analysis,
                capability,
                language,
                context,
                DateTime.UtcNow,
                checked(committed.Workspace.AnalysisGeneration + 1),
                newCompilationHost,
                newCodeExecutor,
                newRefactoring,
                identity: identity);
            Commit(new CommittedGraphSessionState(
                workspace,
                candidateRoslynAdapter,
                resolvedRuleSet,
                candidateExcludePaths));
        }

        return BuildLoadResult(
            mode: "full",
            graph: graph,
            analysis: analysis,
            usage: usage,
            sourceControl: sourceControlSnapshot,
            acceptedChanges: fullRequestedMode == "incremental" && candidateRoslynAdapter != null
                ? candidateRoslynAdapter.CaptureFullFallbackAcceptedChanges()
                : null,
            changeReceipt: changeReceipt,
            skipped: candidateRoslynAdapter?.SkippedFiles,
            packageSourceVisibility: candidateRoslynAdapter?.PackageSourceVisibility,
            requestedMode: fullRequestedMode,
            fallbackReason: fullFallbackReason,
            fallbackDetail: fullFallbackDetail,
            activeProfiles: defineProfiles,
            projectPath: projectPath,
            graphPath: graphPath,
            rulesPath: resolvedRuleSet.Source,
            identity: identity);
    }

    private string LoadIncremental(
        CommittedGraphSessionState committed,
        string projectPath,
        SourceControlSnapshot sourceControlSnapshot,
        string? rulesPath,
        bool allowFullFallback,
        string[]? excludePaths,
        string[]? authoritativeChangedFiles,
        AcceptedChangeReceiptRequest changeReceipt,
        AnalysisKey? expectedAnalysisKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capture = UsageProbe.Start();
        AnalysisUsage? usage = null;
        try
        {
            var effectiveRuleSet = rulesPath == null
                ? RefreshCommittedRuleSet(committed, projectPath)
                : _ruleSetResolver.Resolve(rulesPath, projectPath);
            var config = new AnalysisConfig
            {
                RetainCompilations = true,
                AllowFullFallback = allowFullFallback,
                ExcludePathGlobs = excludePaths == null ? committed.ExcludePaths : NormalizePathGlobs(excludePaths),
                AuthoritativeChangedFiles = authoritativeChangedFiles,
                DefineProfiles = committed.RoslynAdapter!.RetainedProfileNames.ToArray(),
            };
            var candidateAdapter = committed.RoslynAdapter.ForkForIncrementalCandidate();
            IncrementalAnalyzeResult incremental;
            using (TelemetryPhase("incremental-analyze",
                new TelemetryTag("analyze.allow_full_fallback", allowFullFallback)))
            {
                incremental = candidateAdapter.IncrementalAnalyze(config);
            }
            cancellationToken.ThrowIfCancellationRequested();
            capture.MarkPhase("incremental");

            // INV-MULTI-DEFINE-INCREMENTAL-001. Snapshot's retained profile set echoed
            // on every incremental wire path (rejected / noop / incremental / full
            // fallback). Count == 1 collapses to null per BuildLoadResult contract —
            // single-profile back-compat byte-stable. The committed adapter is non-null
            // here per the CanIncremental gate at the public Load entry.
            var incrActiveProfiles = candidateAdapter.RetainedProfileNames is { Count: > 1 } names
                ? names.ToArray()
                : null;

            // INV-ANALYZE-FALLBACK-001: caller refused widening; the adapter
            // returned a typed Rejected. Surface it on the wire — the agent
            // re-runs explicitly with allowFullFallback:true or switches to
            // a non-incremental call.
            if (incremental.Mode == IncrementalMode.Rejected)
            {
                usage = capture.Stop();
                return BuildLoadResult(
                    mode: "rejected",
                    graph: null,
                    analysis: null,
                    usage: usage,
                    sourceControl: sourceControlSnapshot,
                    acceptedChanges: incremental.AcceptedChanges,
                    changeReceipt: changeReceipt,
                    skipped: committed.RoslynAdapter?.SkippedFiles,
                    packageSourceVisibility: committed.RoslynAdapter?.PackageSourceVisibility,
                    requestedMode: "incremental",
                    fallbackReason: incremental.Reason,
                    fallbackDetail: incremental.Detail,
                    canRetryFull: true,
                    activeProfiles: incrActiveProfiles,
                    projectPath: projectPath,
                    rulesPath: effectiveRuleSet.Source,
                    identity: committed.Workspace.Identity);
            }

            // From here on Graph is non-null (Incremental or FullFallback).
            var graph = incremental.Graph!;
            var changedFileCount = incremental.ChangedFileCount;
            var candidateIdentity = BuildRoslynIdentity(
                projectPath,
                candidateAdapter,
                config.ExcludePathGlobs,
                retainCompilations: true,
                effectiveRuleSet.Identity);
            EnsureExpectedAnalysisKey(expectedAnalysisKey, candidateIdentity);

            if (incremental.Mode == IncrementalMode.Incremental && changedFileCount == 0)
            {
                if (committed.Workspace.Identity?.AnalysisKey != candidateIdentity.AnalysisKey)
                {
                    AnalysisResult refreshedAnalysis;
                    using (TelemetryPhase("incremental-rules-analyze"))
                    {
                        refreshedAnalysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, effectiveRuleSet.Rules);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    capture.MarkPhase("rules-analyze");

                    using (TelemetryPhase("incremental-session-commit"))
                    {
                        var workspace = committed.Workspace.DeriveAnalysis(
                            refreshedAnalysis,
                            candidateIdentity,
                            DateTime.UtcNow,
                            checked(committed.Workspace.AnalysisGeneration + 1));
                        Commit(new CommittedGraphSessionState(
                            workspace,
                            candidateAdapter,
                            effectiveRuleSet,
                            config.ExcludePathGlobs));
                    }

                    usage = capture.Stop();
                    return BuildLoadResult(
                        mode: "incremental",
                        graph: graph,
                        analysis: refreshedAnalysis,
                        usage: usage,
                        sourceControl: sourceControlSnapshot,
                        acceptedChanges: incremental.AcceptedChanges,
                        changeReceipt: changeReceipt,
                        skipped: candidateAdapter.SkippedFiles,
                        packageSourceVisibility: candidateAdapter.PackageSourceVisibility,
                        requestedMode: "incremental",
                        activeProfiles: incrActiveProfiles,
                        projectPath: projectPath,
                        rulesPath: effectiveRuleSet.Source,
                        identity: candidateIdentity);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Commit(committed.WithRoslynAdapter(candidateAdapter));
                usage = capture.Stop();
                // Graph is unchanged on noop — reuse the prior session analysis
                // so the response surfaces real modules/types/files/violations/cycles
                // counts instead of zeros. Pre-fix this passed analysis:null and
                // BuildLoadResult fell back to 0 across the board, making
                // incremental-noop responses indistinguishable from "no graph
                // loaded" to anything reading the summary metrics.
                return BuildLoadResult(
                    mode: "incremental-noop",
                    graph: graph,
                    analysis: committed.Workspace.Analysis,
                    usage: usage,
                    sourceControl: sourceControlSnapshot,
                    acceptedChanges: incremental.AcceptedChanges,
                    changeReceipt: changeReceipt,
                    skipped: candidateAdapter.SkippedFiles,
                    packageSourceVisibility: candidateAdapter.PackageSourceVisibility,
                    requestedMode: "incremental",
                    activeProfiles: incrActiveProfiles,
                    projectPath: projectPath,
                    rulesPath: effectiveRuleSet.Source,
                    identity: committed.Workspace.Identity);
            }

            // Validate the rebuilt graph
            GraphValidationError[] errors;
            using (TelemetryPhase("incremental-validate"))
            {
                errors = GraphValidator.Validate(graph);
            }
            if (errors.Length > 0)
            {
                capture.Dispose();
                return $"Incremental graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";
            }

            AnalysisResult analysis;
            using (TelemetryPhase("incremental-rules-analyze"))
            {
                analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, effectiveRuleSet.Rules);
            }
            cancellationToken.ThrowIfCancellationRequested();
            capture.MarkPhase("validate-analyze");

            var (newCompilationHost, newCodeExecutor, newRefactoring) =
                CreateCompilationServices(candidateAdapter, graph, projectPath);

            cancellationToken.ThrowIfCancellationRequested();

            using (TelemetryPhase("incremental-session-commit"))
            {
                var workspace = WorkspaceSnapshot.Create(
                    graph,
                    analysis,
                    candidateAdapter.Capability,
                    "csharp",
                    committed.Workspace.Context,
                    DateTime.UtcNow,
                    checked(committed.Workspace.AnalysisGeneration + 1),
                    newCompilationHost,
                    newCodeExecutor,
                    newRefactoring,
                    identity: candidateIdentity);
                Commit(new CommittedGraphSessionState(
                    workspace,
                    candidateAdapter,
                    effectiveRuleSet,
                    config.ExcludePathGlobs));
            }

            usage = capture.Stop();
            // INV-ANALYZE-FALLBACK-001: `mode` reports what the adapter DID,
            // `requestedMode` reports what the caller ASKED. FullFallback's
            // `mode` is "full" (the truth: the adapter did a full re-analyze)
            // and the `fallbackReason` field surfaces alongside so the caller
            // sees the cache-miss without parsing a hybrid mode value.
            var wireMode = incremental.Mode == IncrementalMode.FullFallback ? "full" : "incremental";
            return BuildLoadResult(
                mode: wireMode,
                graph: graph,
                analysis: analysis,
                usage: usage,
                sourceControl: sourceControlSnapshot,
                acceptedChanges: incremental.AcceptedChanges,
                changeReceipt: changeReceipt,
                skipped: candidateAdapter.SkippedFiles,
                packageSourceVisibility: candidateAdapter.PackageSourceVisibility,
                requestedMode: "incremental",
                fallbackReason: incremental.Reason,
                fallbackDetail: incremental.Detail,
                activeProfiles: incrActiveProfiles,
                projectPath: projectPath,
                rulesPath: effectiveRuleSet.Source,
                identity: candidateIdentity);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the structured JSON response the MCP client receives from
    /// <c>lifeblood_analyze</c>. Always includes the graph summary. Also
    /// includes a <c>usage</c> block when the run was measured (both full
    /// and incremental paths). Agents consume the result as JSON, so the
    /// shape is stable and machine-readable. Humans see the pretty-printed
    /// form through the MCP client's text wrapping.
    /// </summary>
    private static string BuildLoadResult(
        string mode,
        SemanticGraph? graph,
        Lifeblood.Domain.Results.AnalysisResult? analysis,
        AnalysisUsage? usage,
        SourceControlSnapshot sourceControl,
        AcceptedChangeSet? acceptedChanges,
        AcceptedChangeReceiptRequest? changeReceipt = null,
        IReadOnlyList<Lifeblood.Domain.Results.SkippedFile>? skipped = null,
        PackageSourceVisibilityReport? packageSourceVisibility = null,
        string? requestedMode = null,
        FallbackReason? fallbackReason = null,
        string? fallbackDetail = null,
        bool? canRetryFull = null,
        string[]? activeProfiles = null,
        string? projectPath = null,
        string? graphPath = null,
        string? rulesPath = null,
        WorkspaceAnalysisIdentity? identity = null)
    {
        var changedFileCount = acceptedChanges?.ChangedFileCount;
        var mtimeTouchedFileCount = acceptedChanges?.MtimeTouchedFileCount;
        var contentChangedFileCount = acceptedChanges?.ContentChangedFileCount;
        var acceptedChangesField = acceptedChanges == null
            ? null
            : AcceptedChangeReceipt.Build(
                acceptedChanges,
                changeReceipt ?? AcceptedChangeReceiptRequest.Summary);

        // Skipped files surface in the analyze response so users can see
        // exactly which files the adapter dropped and why. Emitted as
        // `skipped` when non-empty, omitted entirely otherwise to keep
        // the common-case response shape lean.
        object? skippedField = null;
        if (skipped != null && skipped.Count > 0)
        {
            skippedField = new
            {
                count = skipped.Count,
                files = skipped.Select(s => new
                {
                    path = s.FilePath,
                    reason = s.Reason,
                    module = s.ModuleName,
                }).ToArray(),
            };
        }

        // INV-ANALYZE-FALLBACK-001 wire shape: `mode` reports what the
        // adapter DID; `requestedMode` separately reports what the caller
        // ASKED for. Disambiguates the fallback case (DID=full + ASKED=
        // incremental) without inventing a hybrid mode value. Fallback
        // reason + detail surface alongside whenever the cheap path could
        // not be honored cleanly. Rejection responses additionally carry
        // `canRetryFull` + `suggestedRetry` so the agent's next move is
        // self-documenting — no out-of-band knowledge required.
        object? suggestedRetry = null;
        if (canRetryFull == true)
        {
            suggestedRetry = new
            {
                incremental = true,
                allowFullFallback = true,
            };
        }

        var response = new
        {
            mode,
            requestedMode,
            summary = graph == null ? null : new
            {
                symbols = graph.Symbols.Count,
                edges = graph.Edges.Count,
                modules = analysis?.Metrics.TotalModules ?? 0,
                types = analysis?.Metrics.TotalTypes ?? 0,
                files = analysis?.Metrics.TotalFiles ?? 0,
                violations = analysis?.Violations.Length ?? 0,
                cycles = analysis?.Cycles.Length ?? 0,
                // INV-MULTI-DEFINE-ANALYZE-001 wire shape.
                profileCount = activeProfiles?.Length ?? 1,
                activeProfiles = activeProfiles,
                perProfileEdgeCounts = activeProfiles == null || activeProfiles.Length <= 1
                    ? null
                    : BuildPerProfileEdgeCounts(graph, activeProfiles),
            },
            fallbackReason = fallbackReason.HasValue ? WireReasonName(fallbackReason.Value) : null,
            fallbackDetail,
            canRetryFull,
            suggestedRetry,
            // Legacy field — kept for back-compat with callers that
            // already read it. The signal is split into the two named
            // fields below; new callers should prefer those.
            changedFileCount,
            changedSourceFiles = changedFileCount,
            touchedGraphFiles = changedFileCount,
            mtimeTouchedSourceFiles = mtimeTouchedFileCount,
            contentChangedSourceFiles = contentChangedFileCount,
            acceptedChanges = acceptedChangesField,
            skipped = skippedField,
            packageSourceVisibility = BuildPackageSourceVisibilityField(packageSourceVisibility),
            analysisIdentity = identity == null
                ? null
                : WorkspaceAnalysisDescriptor.From(identity),
            evidenceReceipt = ServerIdentity.BuildAnalyzeEvidenceReceipt(
                mode,
                requestedMode,
                graph,
                analysis,
                projectPath,
                graphPath,
                rulesPath,
                activeProfiles,
                fallbackReason.HasValue ? WireReasonName(fallbackReason.Value) : null,
                sourceControl),
            usage = usage == null ? null : new
            {
                wallTimeMs = usage.WallTimeMs,
                cpuTimeTotalMs = usage.CpuTimeTotalMs,
                cpuTimeUserMs = usage.CpuTimeUserMs,
                cpuTimeKernelMs = usage.CpuTimeKernelMs,
                cpuUtilizationPercent = Math.Round(usage.CpuUtilizationPercent, 1),
                cpuAvgPerCorePercent = usage.HostLogicalCores > 0
                    ? Math.Round(usage.CpuUtilizationPercent / usage.HostLogicalCores, 2)
                    : 0.0,
                peakWorkingSetBytes = usage.PeakWorkingSetBytes,
                peakWorkingSetMb = Math.Round(usage.PeakWorkingSetBytes / 1024.0 / 1024.0, 0),
                peakPrivateBytesBytes = usage.PeakPrivateBytesBytes,
                peakPrivateBytesMb = Math.Round(usage.PeakPrivateBytesBytes / 1024.0 / 1024.0, 0),
                hostLogicalCores = usage.HostLogicalCores,
                gcGen0Collections = usage.GcGen0Collections,
                gcGen1Collections = usage.GcGen1Collections,
                gcGen2Collections = usage.GcGen2Collections,
                phases = usage.Phases.Select(p => new { name = p.Name, durationMs = p.DurationMs }).ToArray(),
            },
        };
        return JsonSerializer.Serialize(response, JsonOpts);
    }

    private static object? BuildPackageSourceVisibilityField(PackageSourceVisibilityReport? report)
    {
        if (report == null)
            return null;

        const int maxFilesPerPackage = 64;
        return new
        {
            report.IsUnityWorkspace,
            report.DescriptorPaths,
            report.PackageCount,
            report.IncludedSourceFileCount,
            report.ExcludedSourceFileCount,
            report.UnboundSourceFileCount,
            packages = report.Packages.Select(package =>
            {
                var files = package.Files
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .Take(maxFilesPerPackage)
                    .ToArray();
                return new
                {
                    package.Name,
                    package.RootPath,
                    package.DescriptorSources,
                    package.AssemblyDefinitionCount,
                    package.AssemblyDefinitions,
                    package.SourceFileCount,
                    package.IncludedSourceFileCount,
                    package.ExcludedSourceFileCount,
                    package.UnboundSourceFileCount,
                    returnedFileCount = files.Length,
                    omittedFileCount = package.Files.Length - files.Length,
                    truncated = package.Files.Length > files.Length,
                    files,
                };
            }).ToArray(),
        };
    }

    /// <summary>INV-MULTI-DEFINE-ANALYZE-001 per-profile edge count summary.</summary>
    private static Dictionary<string, int> BuildPerProfileEdgeCounts(SemanticGraph graph, string[] activeProfiles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in activeProfiles) counts[name] = 0;
        foreach (var e in graph.Edges)
        {
            if (e.Profiles == null) continue;
            foreach (var p in e.Profiles)
            {
                if (counts.ContainsKey(p)) counts[p]++;
            }
        }
        return counts;
    }

    /// <summary>
    /// Maps <see cref="FallbackReason"/> to its stable wire string. The wire
    /// names are deliberately camelCase (matching the MCP response style) and
    /// distinct from the C# identifier names so the enum can be renamed
    /// internally without breaking callers. INV-ANALYZE-FALLBACK-001.
    /// </summary>
    private static string WireReasonName(FallbackReason reason) => reason switch
    {
        FallbackReason.NoPriorAnalysis => "noPriorAnalysis",
        FallbackReason.ModuleSetChanged => "moduleSetChanged",
        FallbackReason.ModuleDescriptorChanged => "moduleDescriptorChanged",
        FallbackReason.AnalysisScopeChanged => "analysisScopeChanged",
        FallbackReason.CompilationStateUnavailable => "compilationStateUnavailable",
        _ => reason.ToString(),
    };

    private string? BuildCompilationStateRecoveryHint()
    {
        var committed = Current;
        if (committed.Workspace.HasCompilationState) return null;
        if (committed.IsHistorical)
        {
            return "Selected historical snapshots are graph-only and do not retain Roslyn compilation state. "
                   + "Omit snapshotId to use the latest semantic base.";
        }
        if (!committed.Workspace.IsLoaded)
            return "Write-side tools require lifeblood_analyze with projectPath and readOnly:false.";
        var retainedProfile = committed.RoslynAdapter?.RetainedProfileName;
        var retainedProfiles = committed.RoslynAdapter?.RetainedProfileNames ?? Array.Empty<string>();
        if (committed.Workspace.Identity?.Spec.RetentionMode == AnalysisRetentionMode.RetainedSemantic
            && retainedProfiles.Count > 1
            && !string.IsNullOrEmpty(retainedProfile))
        {
            var alternatives = string.Join(", ", retainedProfiles
                .Where(profile => !string.Equals(profile, retainedProfile, StringComparison.Ordinal)));
            var profileHint = string.IsNullOrEmpty(alternatives)
                ? "Re-analyze with the profile that owns the target file first in defineProfiles."
                : $"Re-analyze with the target owning profile first in defineProfiles (for this snapshot, candidates after '{retainedProfile}': {alternatives}).";
            return $"Current graph requested retained semantic state, but retained profile '{retainedProfile}' has no Roslyn compilation state. "
                   + "Write-side tools use only the first retained profile. "
                   + $"{profileHint} Keep readOnly:false.";
        }
        var projectPath = committed.Workspace.Context?.RootPath;
        if (!string.IsNullOrEmpty(projectPath))
            return BuildCompilationStateRecoveryDetail(projectPath);
        return "Loaded graph has no retained Roslyn compilation state. Write-side tools require lifeblood_analyze with projectPath and readOnly:false.";
    }

    private static string BuildCompilationStateRecoveryDetail(string projectPath)
        => "Current graph has no retained Roslyn compilation state, likely because it was loaded with readOnly:true. "
           + $"Recovery: call lifeblood_analyze with projectPath:'{projectPath}', incremental:false, readOnly:false.";

    private static string[] NormalizePathGlobs(string[]? globs)
        => globs?
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim().Replace('\\', '/'))
            .ToArray()
           ?? Array.Empty<string>();

    private static AcceptedChangeSet EmptyAcceptedChanges(string[]? authoritativeChangedFiles)
        => AcceptedChangeSet.Create(
            authoritativeChangedFiles == null
                ? ChangeScanMode.FilesystemPrefilter
                : ChangeScanMode.AuthoritativeChangedSet);

    private static ContentFingerprint BuildExecutionPolicyFingerprint(
        AnalyzeToolRequest request,
        string? projectPath,
        string? graphPath,
        string? effectiveRulesSource)
    {
        var changedFiles = request.AuthoritativeChangedFiles?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeExecutionPath(projectPath, path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray()
            ?? Array.Empty<string>();

        return ContentFingerprint.Compute(
            "lifeblood.analysis-request-policy.v2",
            new[]
            {
                request.Incremental ? "incremental" : "full",
                request.AllowFullFallback ? "allow-fallback" : "reject-fallback",
                request.AuthoritativeChangedFiles == null
                    ? "filesystem-prefilter"
                    : "authoritative-changed-set",
                request.EffectiveChangeReceipt.Mode == AcceptedChangeReceiptMode.Detail
                    ? "change-receipt-detail"
                    : "change-receipt-summary",
                request.EffectiveChangeReceipt.Limit.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                NormalizeExecutionPath(null, projectPath),
                NormalizeExecutionPath(null, graphPath),
                effectiveRulesSource ?? "",
                changedFiles.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }.Concat(changedFiles));
    }

    private static string NormalizeExecutionPath(string? projectPath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectPath ?? throw new InvalidOperationException(
                    "A relative analysis path requires a project root."), path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static WorkspaceAnalysisIdentity BuildRoslynIdentity(
        string projectPath,
        RoslynWorkspaceAnalyzer adapter,
        IReadOnlyList<string> excludePaths,
        bool retainCompilations,
        RuleSetIdentity ruleSet)
    {
        var source = adapter.CurrentSourceFingerprint
            ?? throw new InvalidOperationException("The Roslyn candidate did not publish a source fingerprint.");
        var spec = new AnalysisSpec(
            adapter.RetainedProfileNames,
            excludePaths,
            retainCompilations
                ? AnalysisRetentionMode.RetainedSemantic
                : AnalysisRetentionMode.GraphOnly,
            AnalysisDescriptorPolicy.WorkspaceDiscovery,
            ruleSet);
        return new WorkspaceAnalysisIdentity(
            WorkspacePathIdentity.CreateWorkspaceKey(projectPath),
            spec,
            source);
    }

    private static WorkspaceAnalysisIdentity BuildImportedGraphIdentity(
        string graphPath,
        ContentFingerprint graphContent,
        RuleSetIdentity ruleSet)
    {
        var spec = new AnalysisSpec(
            defineProfiles: null,
            excludePathGlobs: null,
            AnalysisRetentionMode.GraphOnly,
            AnalysisDescriptorPolicy.ImportedGraph,
            ruleSet);
        var source = new SourceFingerprint(
            new[] { new FingerprintEntry("graph", graphContent) },
            Array.Empty<FingerprintEntry>());
        return new WorkspaceAnalysisIdentity(
            WorkspacePathIdentity.CreateWorkspaceKey(Path.GetDirectoryName(Path.GetFullPath(graphPath))!),
            spec,
            source);
    }

    private AnalysisRuleSetResolver.ResolvedRuleSet RefreshCommittedRuleSet(
        CommittedGraphSessionState committed,
        string workspaceRoot)
        => committed.RuleSet.Identity.SourceKind == RuleSetSourceKind.None
            ? committed.RuleSet
            : _ruleSetResolver.Resolve(committed.RuleSet.Source, workspaceRoot);

    private static void EnsureExpectedAnalysisKey(
        AnalysisKey? expected,
        WorkspaceAnalysisIdentity actual)
    {
        if (expected != null && expected != actual.AnalysisKey)
            throw new AnalysisInputChangedException(expected, actual.AnalysisKey);
    }

    private static Domain.Capabilities.AdapterCapability UnknownImportedGraphCapability(string? language)
        => new()
        {
            Language = string.IsNullOrWhiteSpace(language) ? "unknown" : language,
            AdapterName = "unknown-json-graph",
            AdapterVersion = "unknown",
            CanDiscoverSymbols = false,
            TypeResolution = Domain.Capabilities.ConfidenceLevel.None,
            CallResolution = Domain.Capabilities.ConfidenceLevel.None,
            ImplementationResolution = Domain.Capabilities.ConfidenceLevel.None,
            CrossModuleReferences = Domain.Capabilities.ConfidenceLevel.None,
            OverrideResolution = Domain.Capabilities.ConfidenceLevel.None,
        };

    public void Dispose()
    {
        lock (_publicationSync)
        {
            var current = Volatile.Read(ref _current);
            CommitCore(
                CommittedGraphSessionState.CreateEmpty(current.Workspace.AnalysisGeneration),
                retainReplaced: false);
            _snapshotCatalog.Dispose();
        }
    }

    private void Commit(CommittedGraphSessionState candidate)
    {
        lock (_publicationSync)
            CommitCore(candidate, retainReplaced: true);
    }

    private void CommitCore(CommittedGraphSessionState candidate, bool retainReplaced)
    {
        var replaced = Interlocked.Exchange(ref _current, candidate);
        if (ReferenceEquals(replaced.Workspace, candidate.Workspace))
            return;

        var sameWorkspace = replaced.Workspace.Identity?.Workspace
            == candidate.Workspace.Identity?.Workspace;
        if (retainReplaced && replaced.Workspace.IsLoaded && sameWorkspace)
            _snapshotCatalog.Retain(replaced.Workspace);
        else if (!sameWorkspace)
            _snapshotCatalog.Clear();

        replaced.Workspace.Dispose();
    }

    private sealed class CommittedGraphSessionState
    {
        public static CommittedGraphSessionState CreateEmpty(long analysisGeneration = 0)
            => new(
                WorkspaceSnapshot.Empty(analysisGeneration),
                roslynAdapter: null,
                AnalysisRuleSetResolver.ResolvedRuleSet.None,
                Array.Empty<string>());

        public static CommittedGraphSessionState CreateHistorical(WorkspaceSnapshot workspace)
            => new(
                workspace,
                roslynAdapter: null,
                AnalysisRuleSetResolver.ResolvedRuleSet.None,
                Array.Empty<string>(),
                isHistorical: true);

        public CommittedGraphSessionState(
            WorkspaceSnapshot workspace,
            RoslynWorkspaceAnalyzer? roslynAdapter,
            AnalysisRuleSetResolver.ResolvedRuleSet ruleSet,
            IReadOnlyList<string> excludePaths,
            bool isHistorical = false)
        {
            Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            RoslynAdapter = roslynAdapter;
            RuleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
            ExcludePaths = excludePaths.ToArray();
            IsHistorical = isHistorical;
        }

        public WorkspaceSnapshot Workspace { get; }

        public RoslynWorkspaceAnalyzer? RoslynAdapter { get; }

        public AnalysisRuleSetResolver.ResolvedRuleSet RuleSet { get; }

        public string[] ExcludePaths { get; }

        public bool IsHistorical { get; }

        public CommittedGraphSessionState WithRoslynAdapter(RoslynWorkspaceAnalyzer roslynAdapter)
            => new(Workspace, roslynAdapter, RuleSet, ExcludePaths);
    }

    public sealed class WorkspaceSnapshotNotFoundException : InvalidOperationException
    {
        public WorkspaceSnapshotNotFoundException(
            SnapshotId requestedSnapshotId,
            SnapshotId currentSnapshotId,
            IReadOnlyList<SnapshotId> retainedSnapshotIds)
            : base($"Snapshot '{requestedSnapshotId}' is neither current nor retained.")
        {
            RequestedSnapshotId = requestedSnapshotId;
            CurrentSnapshotId = currentSnapshotId;
            RetainedSnapshotIds = retainedSnapshotIds;
        }

        public SnapshotId RequestedSnapshotId { get; }

        public SnapshotId CurrentSnapshotId { get; }

        public IReadOnlyList<SnapshotId> RetainedSnapshotIds { get; }
    }

    public sealed class WorkspaceSnapshotSelectionConflictException : InvalidOperationException
    {
        public WorkspaceSnapshotSelectionConflictException(
            SnapshotId requestedSnapshotId,
            SnapshotId leasedSnapshotId)
            : base("A nested read cannot switch away from its outer leased publication.")
        {
            RequestedSnapshotId = requestedSnapshotId;
            LeasedSnapshotId = leasedSnapshotId;
        }

        public SnapshotId RequestedSnapshotId { get; }

        public SnapshotId LeasedSnapshotId { get; }
    }

    private sealed class GraphSessionReadLease : IDisposable
    {
        private GraphSession? _owner;
        private readonly CommittedGraphSessionState _state;
        private readonly WorkspaceSnapshotLease _snapshotLease;

        public GraphSessionReadLease(
            GraphSession owner,
            CommittedGraphSessionState state,
            WorkspaceSnapshotLease snapshotLease)
        {
            _owner = owner;
            _state = state;
            _snapshotLease = snapshotLease;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null)
                return;

            if (ReferenceEquals(owner._leasedState.Value, _state))
                owner._leasedState.Value = null;
            _snapshotLease.Dispose();
        }
    }

    private sealed class NestedReadLease : IDisposable
    {
        public static readonly NestedReadLease Instance = new();

        public void Dispose()
        {
        }
    }

    private TelemetryPhaseScope TelemetryPhase(string phaseName, params TelemetryTag[] tags)
        => new(_telemetry, phaseName, tags);

    private sealed class TelemetryPhaseScope : IDisposable
    {
        private readonly ITelemetrySink _telemetry;
        private readonly ITelemetryOperation _operation;
        private readonly string _phaseName;
        private readonly TelemetryTag[] _tags;
        private readonly long _allocatedBytesStart;
        private bool _disposed;

        public TelemetryPhaseScope(ITelemetrySink telemetry, string phaseName, TelemetryTag[] tags)
        {
            _telemetry = telemetry;
            _phaseName = phaseName;
            _tags = tags;
            _allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: false);
            _operation = telemetry.StartOperation(
                McpTelemetryEvents.AnalyzePhase,
                new[] { new TelemetryTag("analyze.phase", phaseName) }
                    .Concat(tags)
                    .ToArray());
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - _allocatedBytesStart;
            _operation.SetTag("allocation.bytes", allocatedBytes);
            _telemetry.RecordEvent(
                McpTelemetryEvents.AnalyzePhase,
                new[] { new TelemetryTag("analyze.phase", _phaseName), new TelemetryTag("allocation.bytes", allocatedBytes) }
                    .Concat(_tags)
                    .ToArray());
            _operation.Dispose();
        }
    }

    /// <summary>
    /// Writes analysis progress to stderr so MCP clients can show status.
    /// Stderr is the correct channel — stdout is reserved for JSON-RPC.
    /// </summary>
    private sealed class StderrProgressSink : Application.Ports.Output.IProgressSink
    {
        public void Report(string phase, int current, int total) =>
            Console.Error.WriteLine($"[{current}/{total}] {phase}");
    }
}
