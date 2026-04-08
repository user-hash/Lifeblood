using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Microsoft.CodeAnalysis.CSharp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Holds the current graph session: loaded graph + analysis results.
/// Separate from tool dispatch for single responsibility.
/// </summary>
public sealed class GraphSession
{
    private readonly IFileSystem _fs;

    public GraphSession(IFileSystem fs) => _fs = fs;

    public SemanticGraph? Graph { get; private set; }
    public AnalysisResult? Analysis { get; private set; }
    public AdapterCapability? Capability { get; private set; }
    public string? Language { get; private set; }

    public bool IsLoaded => Graph != null;

    /// <summary>
    /// Write-side Roslyn capabilities. Only available when loaded via projectPath (Roslyn adapter).
    /// Null when loaded from JSON graph (no compilation state).
    /// </summary>
    public ICompilationHost? CompilationHost { get; private set; }
    public ICodeExecutor? CodeExecutor { get; private set; }
    public IWorkspaceRefactoring? Refactoring { get; private set; }
    public bool HasCompilationState => CompilationHost != null;

    public string Load(string? projectPath, string? graphPath, string? rulesPath)
    {
        SemanticGraph graph;
        AdapterCapability? capability = null;
        string language = "unknown";
        ICompilationHost? newCompilationHost = null;
        ICodeExecutor? newCodeExecutor = null;
        IWorkspaceRefactoring? newRefactoring = null;

        if (!string.IsNullOrEmpty(graphPath))
        {
            if (!_fs.FileExists(graphPath))
                return $"Graph file not found: {graphPath}";

            using var stream = _fs.OpenRead(graphPath);
            var doc = new JsonGraphImporter().ImportDocument(stream);
            graph = doc.Graph;
            capability = doc.Adapter;
            language = doc.Language;
        }
        else if (!string.IsNullOrEmpty(projectPath))
        {
            if (!_fs.DirectoryExists(projectPath))
                return $"Project directory not found: {projectPath}";

            var adapter = new RoslynWorkspaceAnalyzer(_fs);
            var result = new AnalyzeWorkspaceUseCase(adapter)
                .Execute(projectPath, new AnalysisConfig());
            graph = result.Graph;
            capability = adapter.Capability;
            language = "csharp";

            // Wire write-side Roslyn capabilities from retained compilations
            if (adapter.Compilations is { Count: > 0 })
            {
                newCompilationHost = new RoslynCompilationHost(adapter.Compilations);
                newCodeExecutor = new RoslynCodeExecutor(adapter.Compilations);
                newRefactoring = new RoslynWorkspaceRefactoring(adapter.Compilations);
            }
        }
        else
        {
            return "Specify projectPath or graphPath";
        }

        // Validate
        var errors = GraphValidator.Validate(graph);
        if (errors.Length > 0)
            return $"Graph validation failed: {errors.Length} errors. First: [{errors[0].Code}] {errors[0].Message}";

        // Analyze
        ArchitectureRule[]? rules = null;
        if (!string.IsNullOrEmpty(rulesPath) && _fs.FileExists(rulesPath))
        {
            var json = _fs.ReadAllText(rulesPath);
            var rulesDoc = System.Text.Json.JsonSerializer.Deserialize<RulesDoc>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            rules = rulesDoc?.Rules;
        }

        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);

        // Commit all state atomically — only after successful validation + analysis
        CompilationHost = newCompilationHost;
        CodeExecutor = newCodeExecutor;
        Refactoring = newRefactoring;
        Graph = graph;
        Capability = capability;
        Language = language;
        Analysis = analysis;

        return $"Loaded: {graph.Symbols.Count} symbols, {graph.Edges.Count} edges, " +
               $"{analysis.Metrics.TotalModules} modules, {analysis.Violations.Length} violations";
    }

    private sealed class RulesDoc
    {
        public ArchitectureRule[]? Rules { get; set; }
    }
}
