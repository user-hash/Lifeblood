using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// One immutable, language-neutral publication of a loaded workspace. Graph,
/// analysis, context, generation, and optional semantic ports always advance
/// together by replacing the containing reference.
/// </summary>
public sealed class WorkspaceSnapshot : IDisposable
{
    private int _disposed;

    private WorkspaceSnapshot(
        SemanticGraph? graph,
        AnalysisResult? analysis,
        AdapterCapability? capability,
        WorkspaceCapability workspaceOps,
        string? language,
        WorkspaceContext? context,
        DateTime? analyzedAtUtc,
        long analysisGeneration,
        ICompilationHost? compilationHost,
        ICodeExecutor? codeExecutor,
        IWorkspaceRefactoring? refactoring)
    {
        Graph = graph;
        Analysis = analysis;
        Capability = capability;
        WorkspaceOps = workspaceOps;
        Language = language;
        Context = context;
        AnalyzedAtUtc = analyzedAtUtc;
        AnalysisGeneration = analysisGeneration;
        CompilationHost = compilationHost;
        CodeExecutor = codeExecutor;
        Refactoring = refactoring;
    }

    public SemanticGraph? Graph { get; }

    public AnalysisResult? Analysis { get; }

    public AdapterCapability? Capability { get; }

    public WorkspaceCapability WorkspaceOps { get; }

    public string? Language { get; }

    public WorkspaceContext? Context { get; }

    public DateTime? AnalyzedAtUtc { get; }

    public long AnalysisGeneration { get; }

    public ICompilationHost? CompilationHost { get; }

    public ICodeExecutor? CodeExecutor { get; }

    public IWorkspaceRefactoring? Refactoring { get; }

    public bool IsLoaded => Graph != null;

    public bool HasCompilationState => CompilationHost != null;

    public static WorkspaceSnapshot Create(
        SemanticGraph graph,
        AnalysisResult analysis,
        AdapterCapability? capability,
        string? language,
        WorkspaceContext? context,
        DateTime analyzedAtUtc,
        long analysisGeneration,
        ICompilationHost? compilationHost = null,
        ICodeExecutor? codeExecutor = null,
        IWorkspaceRefactoring? refactoring = null,
        WorkspaceCapability? workspaceOps = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysisGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(analysisGeneration), "A loaded snapshot generation must be positive.");

        var compilationServiceCount =
            (compilationHost == null ? 0 : 1)
            + (codeExecutor == null ? 0 : 1)
            + (refactoring == null ? 0 : 1);
        if (compilationServiceCount is not (0 or 3))
            throw new ArgumentException("Compilation host, code executor, and refactoring ports must be published together.");

        return new WorkspaceSnapshot(
            graph,
            analysis,
            capability,
            compilationServiceCount == 0
                ? WorkspaceCapability.None
                : workspaceOps ?? WorkspaceCapability.RoslynFull,
            language,
            context,
            analyzedAtUtc,
            analysisGeneration,
            compilationHost,
            codeExecutor,
            refactoring);
    }

    public static WorkspaceSnapshot Empty(long analysisGeneration = 0)
    {
        if (analysisGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(analysisGeneration));

        return new WorkspaceSnapshot(
            graph: null,
            analysis: null,
            capability: null,
            WorkspaceCapability.None,
            language: null,
            context: null,
            analyzedAtUtc: null,
            analysisGeneration,
            compilationHost: null,
            codeExecutor: null,
            refactoring: null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        DisposeOnce(CompilationHost, disposed);
        DisposeOnce(CodeExecutor, disposed);
        DisposeOnce(Refactoring, disposed);
    }

    private static void DisposeOnce(object? value, HashSet<object> disposed)
    {
        if (value is IDisposable disposable && disposed.Add(value))
            disposable.Dispose();
    }
}
