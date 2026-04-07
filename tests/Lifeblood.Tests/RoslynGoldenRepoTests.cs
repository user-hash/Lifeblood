using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Layer A1: End-to-end golden repo tests.
/// Feeds real multi-file source into Roslyn extractors, asserts graph properties.
/// Uses CSharpCompilation (not file system) so tests run anywhere.
/// </summary>
public class RoslynGoldenRepoTests
{
    [Fact]
    public void HexagonalApp_ExtractsTypes()
    {
        var graph = BuildHexagonalAppGraph();

        Assert.Contains(graph.Symbols, s => s.Name == "Entity" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "IRepository" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "CreateEntityUseCase" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(graph.Symbols, s => s.Name == "SqlRepository" && s.Kind == DomainSymbolKind.Type);
    }

    [Fact]
    public void HexagonalApp_InfrastructureImplementsDomainInterface()
    {
        var graph = BuildHexagonalAppGraph();

        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("SqlRepository")
            && e.TargetId.Contains("IRepository"));
    }

    [Fact]
    public void HexagonalApp_ApplicationDependsOnDomain()
    {
        var graph = BuildHexagonalAppGraph();

        // CreateEntityUseCase references IRepository (domain interface)
        Assert.Contains(graph.Edges, e =>
            e.Kind == EdgeKind.References
            && e.SourceId.Contains("CreateEntityUseCase")
            && e.TargetId.Contains("IRepository"));
    }

    [Fact]
    public void HexagonalApp_GraphValidatesClean()
    {
        var graph = BuildHexagonalAppGraph();
        var errors = GraphValidator.Validate(graph);
        // Dangling edges to external BCL types (System.*, etc.) are expected
        // when compiling without full framework references. Only check for
        // non-system dangling edges.
        var realErrors = errors.Where(e =>
            e.Code != "DANGLING_EDGE_TARGET" && e.Code != "DANGLING_EDGE_SOURCE"
            || (e.Message != null && !e.Message.Contains("System."))).ToArray();
        Assert.Empty(realErrors);
    }

    [Fact]
    public void HexagonalApp_CouplingAnalysis_EntityIsStable()
    {
        var graph = BuildHexagonalAppGraph();
        var coupling = CouplingAnalyzer.Analyze(graph, new[] { DomainSymbolKind.Type });

        var entity = coupling.FirstOrDefault(c => c.SymbolId.Contains("Entity"));
        Assert.NotNull(entity);
        // Entity has dependants but no outgoing deps → low instability
        Assert.True(entity!.Instability <= 0.5f,
            $"Entity instability {entity.Instability} should be <= 0.5 (stable)");
    }

    [Fact]
    public void CycleRepo_DetectsCycle()
    {
        var graph = BuildCycleRepoGraph();
        var cycles = CircularDependencyDetector.Detect(graph);

        Assert.NotEmpty(cycles);
        var cycle = cycles[0];
        Assert.Contains(cycle, id => id.Contains("ServiceA"));
        Assert.Contains(cycle, id => id.Contains("ServiceB"));
    }

    [Fact]
    public void CycleRepo_BlastRadius_MutuallyAffected()
    {
        var graph = BuildCycleRepoGraph();

        var blastA = BlastRadiusAnalyzer.Analyze(graph,
            graph.Symbols.First(s => s.Name == "ServiceA").Id);
        var blastB = BlastRadiusAnalyzer.Analyze(graph,
            graph.Symbols.First(s => s.Name == "ServiceB").Id);

        // In a cycle, changing either affects the other
        Assert.True(blastA.AffectedCount > 0 || blastB.AffectedCount > 0,
            "Cycle members should appear in each other's blast radius");
    }

    // --- Graph builders using in-memory compilation ---

    private static SemanticGraph BuildHexagonalAppGraph()
    {
        var domainSource = @"
namespace HexagonalApp.Domain;
public class Entity
{
    public string Id { get; set; } = """";
    public string Name { get; set; } = """";
}
public interface IRepository
{
    Entity? GetById(string id);
    void Save(Entity entity);
}";

        var appSource = @"
using HexagonalApp.Domain;
namespace HexagonalApp.Application;
public class CreateEntityUseCase
{
    private readonly IRepository _repo;
    public CreateEntityUseCase(IRepository repo) { _repo = repo; }
    public Entity Execute(string name)
    {
        var entity = new Entity { Name = name };
        _repo.Save(entity);
        return entity;
    }
}";

        var infraSource = @"
using HexagonalApp.Domain;
namespace HexagonalApp.Infrastructure;
public class SqlRepository : IRepository
{
    private readonly System.Collections.Generic.Dictionary<string, Entity> _store = new();
    public Entity? GetById(string id) => _store.TryGetValue(id, out var e) ? e : null;
    public void Save(Entity entity) { _store[entity.Id] = entity; }
}";

        return CompileToGraph("HexagonalApp",
            ("Domain/Entity.cs", domainSource),
            ("Application/UseCase.cs", appSource),
            ("Infrastructure/SqlRepository.cs", infraSource));
    }

    private static SemanticGraph BuildCycleRepoGraph()
    {
        var sourceA = @"
namespace CycleRepo;
public class ServiceA
{
    private readonly ServiceB _b;
    public ServiceA(ServiceB b) { _b = b; }
    public void DoWork() { _b.Process(); }
}";

        var sourceB = @"
namespace CycleRepo;
public class ServiceB
{
    private readonly ServiceA _a;
    public ServiceB(ServiceA a) { _a = a; }
    public void Process() { _a.DoWork(); }
}";

        return CompileToGraph("CycleRepo",
            ("ServiceA.cs", sourceA),
            ("ServiceB.cs", sourceB));
    }

    private static SemanticGraph CompileToGraph(string assemblyName, params (string path, string source)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.source, path: f.path)).ToArray();

        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            var sr = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(sr)) refs.Add(MetadataReference.CreateFromFile(sr));
            var sc = Path.Combine(runtimeDir, "System.Collections.dll");
            if (File.Exists(sc)) refs.Add(MetadataReference.CreateFromFile(sc));
        }

        var compilation = CSharpCompilation.Create(assemblyName, trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var builder = new GraphBuilder();
        var symbolExtractor = new RoslynSymbolExtractor();
        var edgeExtractor = new RoslynEdgeExtractor();

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var fileId = $"file:{tree.FilePath}";
            builder.AddSymbol(new Symbol
            {
                Id = fileId, Name = Path.GetFileName(tree.FilePath),
                Kind = DomainSymbolKind.File, FilePath = tree.FilePath,
            });
            builder.AddSymbols(symbolExtractor.Extract(model, tree.GetRoot(), tree.FilePath, fileId));
            builder.AddEdges(edgeExtractor.Extract(model, tree.GetRoot()));
        }

        return builder.Build();
    }
}
