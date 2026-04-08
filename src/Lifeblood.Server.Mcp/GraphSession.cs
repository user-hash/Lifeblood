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
    private readonly IFileSystem _fs;
    private readonly WorkspaceSession _session = new();
    private RoslynWorkspaceAnalyzer? _roslynAdapter;
    private string? _lastProjectPath;
    private string? _lastRulesPath;

    public GraphSession(IFileSystem fs) => _fs = fs;

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

    public string Load(string? projectPath, string? graphPath, string? rulesPath, bool incremental = false)
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
            var result = new AnalyzeWorkspaceUseCase(adapter)
                .Execute(projectPath, new AnalysisConfig { RetainCompilations = true });
            graph = result.Graph;
            capability = adapter.Capability;
            language = "csharp";

            // Wire write-side Roslyn capabilities from retained compilations.
            // Uses in-process RoslynCodeExecutor (trusted-local sandbox).
            // For process-isolated execution, swap to ProcessIsolatedCodeExecutor.
            if (adapter.Compilations is { Count: > 0 })
            {
                newCompilationHost = new RoslynCompilationHost(adapter.Compilations);
                newCodeExecutor = new RoslynCodeExecutor(adapter.Compilations);
                newRefactoring = new RoslynWorkspaceRefactoring(adapter.Compilations);
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

        return $"Loaded: {graph.Symbols.Count} symbols, {graph.Edges.Count} edges, " +
               $"{analysis.Metrics.TotalModules} modules, {analysis.Violations.Length} violations";
    }

    private string LoadIncremental(string projectPath, string? rulesPath)
    {
        var config = new AnalysisConfig { RetainCompilations = true };
        var (graph, changedFileCount) = _roslynAdapter!.IncrementalAnalyze(config);

        if (changedFileCount == 0)
            return $"Incremental: 0 files changed. Graph unchanged ({graph.Symbols.Count} symbols, {graph.Edges.Count} edges)";

        // Validate the rebuilt graph
        var errors = GraphValidator.Validate(graph);
        if (errors.Length > 0)
            return $"Incremental graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";

        // Rebuild write-side services from updated compilations
        ICompilationHost? newCompilationHost = null;
        ICodeExecutor? newCodeExecutor = null;
        IWorkspaceRefactoring? newRefactoring = null;

        if (_roslynAdapter.Compilations is { Count: > 0 })
        {
            newCompilationHost = new RoslynCompilationHost(_roslynAdapter.Compilations);
            newCodeExecutor = new RoslynCodeExecutor(_roslynAdapter.Compilations);
            newRefactoring = new RoslynWorkspaceRefactoring(_roslynAdapter.Compilations);
        }

        ArchitectureRule[]? rules = ResolveRules(rulesPath ?? _lastRulesPath);
        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);

        _session.Clear();
        _session.Load(graph, analysis, _roslynAdapter.Capability, "csharp");
        if (newCompilationHost != null)
            _session.AttachCompilationServices(newCompilationHost!, newCodeExecutor!, newRefactoring!);

        return $"Incremental: {changedFileCount} files changed. Loaded: {graph.Symbols.Count} symbols, {graph.Edges.Count} edges, " +
               $"{analysis.Metrics.TotalModules} modules, {analysis.Violations.Length} violations";
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
}
