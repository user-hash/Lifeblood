using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Output;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Direct tests for application-layer use cases.
/// Uses hand-rolled stubs — no mocking framework needed for these thin interfaces.
/// </summary>
public class UseCaseTests
{
    // ── AnalyzeWorkspaceUseCase ──────────────────────────────────────

    [Fact]
    public void AnalyzeWorkspace_ReturnsGraphFromAdapter()
    {
        var graph = BuildTestGraph();
        var adapter = new StubAnalyzer(graph);
        var useCase = new AnalyzeWorkspaceUseCase(adapter);

        var result = useCase.Execute("/fake/path", new AnalysisConfig());

        Assert.Same(graph, result.Graph);
    }

    [Fact]
    public void AnalyzeWorkspace_PreservesAdapterCapability()
    {
        var adapter = new StubAnalyzer(BuildTestGraph());
        var useCase = new AnalyzeWorkspaceUseCase(adapter);

        var result = useCase.Execute("/fake/path", new AnalysisConfig());

        Assert.Same(adapter.Capability, result.Capability);
        Assert.Equal(ConfidenceLevel.Proven, result.Capability.TypeResolution);
    }

    [Fact]
    public void AnalyzeWorkspace_PassesProjectRootToAdapter()
    {
        var adapter = new StubAnalyzer(BuildTestGraph());
        var useCase = new AnalyzeWorkspaceUseCase(adapter);

        useCase.Execute("/my/project", new AnalysisConfig());

        Assert.Equal("/my/project", adapter.LastProjectRoot);
    }

    [Fact]
    public void AnalyzeWorkspace_PassesConfigToAdapter()
    {
        var adapter = new StubAnalyzer(BuildTestGraph());
        var useCase = new AnalyzeWorkspaceUseCase(adapter);
        var config = new AnalysisConfig { ExcludePatterns = new[] { "bin", "obj" } };

        useCase.Execute("/fake", config);

        Assert.Same(config, adapter.LastConfig);
    }

    [Fact]
    public void AnalyzeWorkspace_ReportsProgressWhenSinkProvided()
    {
        var adapter = new StubAnalyzer(BuildTestGraph());
        var sink = new StubProgressSink();
        var useCase = new AnalyzeWorkspaceUseCase(adapter, sink);

        useCase.Execute("/fake", new AnalysisConfig());

        Assert.True(sink.Reports.Count >= 2, "Expected at least start and complete progress reports");
        Assert.Equal("Analyzing workspace", sink.Reports[0].phase);
        Assert.Equal("Complete", sink.Reports[^1].phase);
    }

    [Fact]
    public void AnalyzeWorkspace_WorksWithoutProgressSink()
    {
        var adapter = new StubAnalyzer(BuildTestGraph());
        var useCase = new AnalyzeWorkspaceUseCase(adapter);

        var result = useCase.Execute("/fake", new AnalysisConfig());

        Assert.NotNull(result.Graph);
    }

    [Fact]
    public void AnalyzeWorkspace_ThrowsOnInvalidGraph()
    {
        // Build a graph with a duplicate symbol ID — validator should catch it
        var badGraph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A_duplicate", Kind = SymbolKind.Type })
            .Build();

        // GraphBuilder deduplicates by ID, so duplicates won't reach the validator.
        // Test with an empty-name symbol instead.
        var emptyNameGraph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "", Kind = SymbolKind.Type })
            .Build();

        var adapter = new StubAnalyzer(emptyNameGraph);
        var useCase = new AnalyzeWorkspaceUseCase(adapter);

        var ex = Assert.Throws<InvalidOperationException>(
            () => useCase.Execute("/fake", new AnalysisConfig()));
        Assert.Contains("Graph validation failed", ex.Message);
        Assert.Contains("EMPTY_SYMBOL_NAME", ex.Message);
    }

    // ── GenerateContextUseCase ───────────────────────────────────────

    [Fact]
    public void GenerateContext_DelegatesToGenerator()
    {
        var pack = new AgentContextPack { Hotspots = new[] { "test-hotspot" } };
        var generator = new StubContextGenerator(pack);
        var useCase = new GenerateContextUseCase(generator);
        var graph = BuildTestGraph();
        var analysis = new AnalysisResult();

        var result = useCase.Execute(graph, analysis);

        Assert.Same(pack, result);
        Assert.Contains("test-hotspot", result.Hotspots);
    }

    [Fact]
    public void GenerateContext_PassesGraphAndAnalysis()
    {
        var generator = new StubContextGenerator(new AgentContextPack());
        var useCase = new GenerateContextUseCase(generator);
        var graph = BuildTestGraph();
        var analysis = new AnalysisResult { Cycles = new[] { new[] { "a", "b" } } };

        useCase.Execute(graph, analysis);

        Assert.Same(graph, generator.LastGraph);
        Assert.Same(analysis, generator.LastAnalysis);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static SemanticGraph BuildTestGraph() =>
        new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Test", Name = "Test", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Test.Foo", Name = "Foo", Kind = SymbolKind.Type, ParentId = "mod:Test" })
            .Build();

    private sealed class StubAnalyzer : IWorkspaceAnalyzer
    {
        private readonly SemanticGraph _graph;

        public StubAnalyzer(SemanticGraph graph) => _graph = graph;

        public AdapterCapability Capability { get; } = new()
        {
            CanDiscoverSymbols = true,
            TypeResolution = ConfidenceLevel.Proven,
        };

        public string? LastProjectRoot { get; private set; }
        public AnalysisConfig? LastConfig { get; private set; }

        public SemanticGraph AnalyzeWorkspace(string projectRoot, AnalysisConfig config)
        {
            LastProjectRoot = projectRoot;
            LastConfig = config;
            return _graph;
        }
    }

    private sealed class StubProgressSink : IProgressSink
    {
        public List<(string phase, int current, int total)> Reports { get; } = new();

        public void Report(string phase, int current, int total) =>
            Reports.Add((phase, current, total));
    }

    private sealed class StubContextGenerator : IAgentContextGenerator
    {
        private readonly AgentContextPack _pack;

        public StubContextGenerator(AgentContextPack pack) => _pack = pack;

        public SemanticGraph? LastGraph { get; private set; }
        public AnalysisResult? LastAnalysis { get; private set; }

        public AgentContextPack Generate(SemanticGraph graph, AnalysisResult analysis)
        {
            LastGraph = graph;
            LastAnalysis = analysis;
            return _pack;
        }
    }

    // ── WorkspaceSession ────────────────────────────────────────────

    [Fact]
    public void WorkspaceSession_InitialState_NotLoaded()
    {
        var session = new WorkspaceSession();
        Assert.False(session.IsLoaded);
        Assert.False(session.HasCompilationState);
        Assert.Null(session.Graph);
        Assert.Equal(WorkspaceCapability.None, session.WorkspaceOps);
    }

    [Fact]
    public void WorkspaceSession_Load_SetsState()
    {
        var session = new WorkspaceSession();
        var graph = BuildTestGraph();
        var analysis = new AnalysisResult();
        var cap = new AdapterCapability { Language = "test" };

        session.Load(graph, analysis, cap, "test");

        Assert.True(session.IsLoaded);
        Assert.Same(graph, session.Graph);
        Assert.Same(analysis, session.Analysis);
        Assert.Equal("test", session.Language);
    }

    [Fact]
    public void WorkspaceSession_AttachCompilation_SetsCapabilities()
    {
        var session = new WorkspaceSession();
        session.Load(BuildTestGraph(), new AnalysisResult(), null, "test");

        var host = new StubCompilationHost();
        var executor = new StubCodeExecutor();
        var refactoring = new StubRefactoring();

        session.AttachCompilationServices(host, executor, refactoring);

        Assert.True(session.HasCompilationState);
        Assert.Same(host, session.CompilationHost);
        Assert.Same(executor, session.CodeExecutor);
        Assert.Same(refactoring, session.Refactoring);
        Assert.True(session.WorkspaceOps.CanExecute);
        Assert.Equal("trusted-local", session.WorkspaceOps.ExecutionTrustLevel);
    }

    [Fact]
    public void WorkspaceSession_Clear_ResetsAllState()
    {
        var session = new WorkspaceSession();
        session.Load(BuildTestGraph(), new AnalysisResult(), null, "test");
        session.AttachCompilationServices(new StubCompilationHost(), new StubCodeExecutor(), new StubRefactoring());

        session.Clear();

        Assert.False(session.IsLoaded);
        Assert.False(session.HasCompilationState);
        Assert.Null(session.Graph);
        Assert.False(session.WorkspaceOps.CanExecute);
    }

    private sealed class StubCompilationHost : ICompilationHost
    {
        public bool IsAvailable => true;
        public DiagnosticInfo[] GetDiagnostics(string? moduleName = null) => Array.Empty<DiagnosticInfo>();
        public CompileCheckResult CompileCheck(string code, string? moduleName = null) => new() { Success = true };
        public ReferenceLocation[] FindReferences(string symbolId) => Array.Empty<ReferenceLocation>();
        public DefinitionLocation? FindDefinition(string symbolId) => null;
        public string[] FindImplementations(string symbolId) => Array.Empty<string>();
        public SymbolAtPosition? GetSymbolAtPosition(string filePath, int line, int column) => null;
        public string GetDocumentation(string symbolId) => "";
    }

    private sealed class StubCodeExecutor : ICodeExecutor
    {
        public CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000)
            => new() { Success = true };
    }

    private sealed class StubRefactoring : IWorkspaceRefactoring
    {
        public TextEdit[] Rename(string symbolId, string newName) => Array.Empty<TextEdit>();
        public string Format(string code) => code;
    }
}
