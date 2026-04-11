using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP-specific session wrapper. Delegates state to WorkspaceSession.
/// Owns the load orchestration: parse args → build graph → validate → analyze → attach services.
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
    private readonly WorkspaceSession _session = new();
    private RoslynWorkspaceAnalyzer? _roslynAdapter;
    private string? _lastProjectPath;
    private string? _lastRulesPath;

    public GraphSession(IFileSystem fs) => _fs = fs;

    /// <summary>
    /// Project root of the most recent Roslyn <c>lifeblood_analyze</c>
    /// call. Empty when the session was loaded from a JSON graph or no
    /// project was loaded at all. Exposed so the MCP tool layer can
    /// resolve relative file paths for features like
    /// <c>lifeblood_partial_view</c>, which reads source off disk.
    /// </summary>
    public string ProjectRoot => _lastProjectPath ?? "";

    /// <summary>Exposed file-system port for tool handlers that need disk access (partial view, compile_check auto-refresh).</summary>
    public IFileSystem FileSystem => _fs;

    // Delegate all state queries to the unified session
    public SemanticGraph? Graph => _session.Graph;
    public AnalysisResult? Analysis => _session.Analysis;
    public bool IsLoaded => _session.IsLoaded;
    public ICompilationHost? CompilationHost => _session.CompilationHost;
    public ICodeExecutor? CodeExecutor => _session.CodeExecutor;
    public IWorkspaceRefactoring? Refactoring => _session.Refactoring;
    public bool HasCompilationState => _session.HasCompilationState;

    /// <summary>True if the session has a previous Roslyn analysis that supports incremental update.</summary>
    public bool CanIncremental => _roslynAdapter?.HasSnapshot == true;

    /// <summary>
    /// Refresh the session if any tracked file has changed on disk since
    /// the last analyze. Idempotent: returns <c>null</c> when nothing
    /// changed, otherwise the number of files that were re-analyzed.
    /// Fails silently on non-Roslyn sessions (JSON graph imports) since
    /// those have no source on disk to diff against. Used by Phase 7 /
    /// DAWG B2 to keep <c>lifeblood_compile_check</c> from running
    /// against a stale workspace after the user edits source between
    /// the initial <c>lifeblood_analyze</c> and the next compile_check.
    /// </summary>
    public int? MaybeRefreshIfStale()
    {
        if (_roslynAdapter == null || !CanIncremental || string.IsNullOrEmpty(_lastProjectPath))
            return null;
        try
        {
            var config = new AnalysisConfig { RetainCompilations = true };
            var (graph, changedFileCount) = _roslynAdapter.IncrementalAnalyze(config);
            if (changedFileCount == 0) return null;

            // Source changed — rebuild the session view. This mirrors the
            // LoadIncremental happy path but skips the response-building.
            var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, null);

            ICompilationHost? newCompilationHost = null;
            ICodeExecutor? newCodeExecutor = null;
            IWorkspaceRefactoring? newRefactoring = null;
            if (_roslynAdapter.Compilations is { Count: > 0 })
            {
                var view = new RoslynSemanticView(
                    _roslynAdapter.Compilations,
                    graph,
                    _roslynAdapter.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));
                newCompilationHost = new RoslynCompilationHost(_roslynAdapter.Compilations, _roslynAdapter.ModuleDependencies);
                newCodeExecutor = new RoslynCodeExecutor(view);
                newRefactoring = new RoslynWorkspaceRefactoring(_roslynAdapter.Compilations, _roslynAdapter.ModuleDependencies);
            }

            _session.Clear();
            _session.Load(graph, analysis, _roslynAdapter.Capability, "csharp");
            if (newCompilationHost != null)
                _session.AttachCompilationServices(newCompilationHost!, newCodeExecutor!, newRefactoring!);

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

    public string Load(string? projectPath, string? graphPath, string? rulesPath, bool incremental = false, bool readOnly = false)
    {
        // Incremental path: reuse existing adapter, only recompile changed modules
        if (incremental && CanIncremental
            && !string.IsNullOrEmpty(projectPath)
            && string.Equals(projectPath, _lastProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            return LoadIncremental(projectPath, rulesPath);
        }

        SemanticGraph graph;
        Domain.Capabilities.AdapterCapability? capability = null;
        string language = "unknown";
        AnalysisUsage? usage = null;
        ICompilationHost? newCompilationHost = null;
        ICodeExecutor? newCodeExecutor = null;
        IWorkspaceRefactoring? newRefactoring = null;

        if (!string.IsNullOrEmpty(graphPath))
        {
            // JSON graph path: import + validate here (no use case involved)
            if (!_fs.FileExists(graphPath))
                return $"Graph file not found: {graphPath}";

            using var stream = _fs.OpenRead(graphPath);
            var doc = new JsonGraphImporter().ImportDocument(stream);
            graph = doc.Graph;
            capability = doc.Adapter;
            language = doc.Language;

            // Validate — JSON graphs don't go through AnalyzeWorkspaceUseCase
            var errors = GraphValidator.Validate(graph);
            if (errors.Length > 0)
                return $"Graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";

            _roslynAdapter = null;
            _lastProjectPath = null;
        }
        else if (!string.IsNullOrEmpty(projectPath))
        {
            // Roslyn path: AnalyzeWorkspaceUseCase validates internally
            if (!_fs.DirectoryExists(projectPath))
                return $"Project directory not found: {projectPath}";

            var adapter = new RoslynWorkspaceAnalyzer(_fs);
            var retainCompilations = !readOnly;
            var progress = new StderrProgressSink();
            adapter.OnModuleProgress = (name, i, total) =>
                Console.Error.WriteLine($"[{i}/{total}] Compiling {name}");
            var result = new AnalyzeWorkspaceUseCase(adapter, progress, UsageProbe)
                .Execute(projectPath, new AnalysisConfig { RetainCompilations = retainCompilations });
            graph = result.Graph;
            capability = adapter.Capability;
            language = "csharp";
            usage = result.Usage;

            // Wire write-side Roslyn capabilities from retained compilations.
            // Uses in-process RoslynCodeExecutor (trusted-local sandbox).
            // For process-isolated execution, swap to ProcessIsolatedCodeExecutor.
            if (adapter.Compilations is { Count: > 0 })
            {
                // Plan v4 Seam #3 / INV-VIEW-002: build the typed read-only view
                // ONCE and share it by reference across consumers. Today the only
                // consumer is the script host (RoslynCodeExecutor); future
                // consumers (debuggers, visualizers, custom linters) reuse the
                // same view via dependency injection from this construction site.
                var view = new RoslynSemanticView(
                    adapter.Compilations,
                    graph,
                    adapter.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

                newCompilationHost = new RoslynCompilationHost(adapter.Compilations, adapter.ModuleDependencies);
                newCodeExecutor = new RoslynCodeExecutor(view);
                newRefactoring = new RoslynWorkspaceRefactoring(adapter.Compilations, adapter.ModuleDependencies);
            }

            // Retain adapter for incremental re-analyze
            _roslynAdapter = adapter;
            _lastProjectPath = projectPath;
        }
        else
        {
            return "Specify projectPath or graphPath";
        }

        // Analyze (rules are optional — resolve built-in name first, then file path)
        ArchitectureRule[]? rules = ResolveRules(rulesPath);
        _lastRulesPath = rulesPath;

        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);

        // Commit atomically via WorkspaceSession
        _session.Clear();
        _session.Load(graph, analysis, capability, language);
        if (newCompilationHost != null)
            _session.AttachCompilationServices(newCompilationHost!, newCodeExecutor!, newRefactoring!);

        return BuildLoadResult(
            mode: "full",
            graph: graph,
            analysis: analysis,
            usage: usage,
            changedFileCount: null,
            skipped: _roslynAdapter?.SkippedFiles);
    }

    private string LoadIncremental(string projectPath, string? rulesPath)
    {
        var capture = UsageProbe.Start();
        AnalysisUsage? usage = null;
        try
        {
        var config = new AnalysisConfig { RetainCompilations = true };
        var (graph, changedFileCount) = _roslynAdapter!.IncrementalAnalyze(config);
        capture.MarkPhase("incremental");

        if (changedFileCount == 0)
        {
            usage = capture.Stop();
            return BuildLoadResult(
                mode: "incremental-noop",
                graph: graph,
                analysis: null,
                usage: usage,
                changedFileCount: 0,
                skipped: _roslynAdapter?.SkippedFiles);
        }

        // Validate the rebuilt graph
        var errors = GraphValidator.Validate(graph);
        if (errors.Length > 0)
        {
            capture.Dispose();
            return $"Incremental graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";
        }

        // Rebuild write-side services from updated compilations
        ICompilationHost? newCompilationHost = null;
        ICodeExecutor? newCodeExecutor = null;
        IWorkspaceRefactoring? newRefactoring = null;

        if (_roslynAdapter.Compilations is { Count: > 0 })
        {
            // Plan v4 Seam #3 — same view construction as the full-load path.
            var view = new RoslynSemanticView(
                _roslynAdapter.Compilations,
                graph,
                _roslynAdapter.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

            newCompilationHost = new RoslynCompilationHost(_roslynAdapter.Compilations, _roslynAdapter.ModuleDependencies);
            newCodeExecutor = new RoslynCodeExecutor(view);
            newRefactoring = new RoslynWorkspaceRefactoring(_roslynAdapter.Compilations, _roslynAdapter.ModuleDependencies);
        }

        ArchitectureRule[]? rules = ResolveRules(rulesPath ?? _lastRulesPath);
        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);
        capture.MarkPhase("validate-analyze");

        _session.Clear();
        _session.Load(graph, analysis, _roslynAdapter.Capability, "csharp");
        if (newCompilationHost != null)
            _session.AttachCompilationServices(newCompilationHost!, newCodeExecutor!, newRefactoring!);

        usage = capture.Stop();
        return BuildLoadResult(
            mode: "incremental",
            graph: graph,
            analysis: analysis,
            usage: usage,
            changedFileCount: changedFileCount,
            skipped: _roslynAdapter?.SkippedFiles);
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
        SemanticGraph graph,
        Lifeblood.Domain.Results.AnalysisResult? analysis,
        AnalysisUsage? usage,
        int? changedFileCount,
        IReadOnlyList<Lifeblood.Domain.Results.SkippedFile>? skipped = null)
    {
        // Phase 4 / C4: skipped files surface in the analyze response so
        // users can see exactly which files the adapter dropped and why.
        // Emitted as `skipped` when non-empty, omitted entirely otherwise
        // to keep the common-case response shape lean.
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

        var response = new
        {
            mode,
            summary = new
            {
                symbols = graph.Symbols.Count,
                edges = graph.Edges.Count,
                modules = analysis?.Metrics.TotalModules ?? 0,
                types = analysis?.Metrics.TotalTypes ?? 0,
                files = analysis?.Metrics.TotalFiles ?? 0,
                violations = analysis?.Violations.Length ?? 0,
                cycles = analysis?.Cycles.Length ?? 0,
            },
            changedFileCount,
            skipped = skippedField,
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

    private ArchitectureRule[]? ResolveRules(string? rulesPath)
    {
        if (string.IsNullOrEmpty(rulesPath)) return null;
        var rules = Lifeblood.Analysis.RulePacks.ResolveBuiltIn(rulesPath);
        if (rules == null && _fs.FileExists(rulesPath))
            rules = Lifeblood.Analysis.RulePacks.ParseJson(_fs.ReadAllText(rulesPath));
        return rules;
    }

    public void Dispose() => _session.Clear();

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
