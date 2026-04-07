using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;
using Xunit;

namespace Lifeblood.Tests;

public class RuleValidatorTests
{
    [Fact]
    public void Validate_NoRules_NoViolations()
    {
        var graph = MakeHexagonalGraph();
        var violations = RuleValidator.Validate(graph, Array.Empty<ArchitectureRule>());
        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_CompliantGraph_NoViolations()
    {
        var graph = MakeHexagonalGraph();
        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "HEX-001",
                Source = "App.Domain",
                MustNotReference = "App.Infrastructure",
            },
        };

        var violations = RuleValidator.Validate(graph, rules);
        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_MustNotReference_CatchesViolation()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "s1", Name = "DomainService", QualifiedName = "App.Domain.DomainService", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "s2", Name = "Database", QualifiedName = "App.Infrastructure.Database", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "s1", TargetId = "s2", Kind = EdgeKind.References })
            .Build();

        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "HEX-001",
                Source = "App.Domain",
                MustNotReference = "App.Infrastructure",
            },
        };

        var violations = RuleValidator.Validate(graph, rules);
        Assert.Single(violations);
        Assert.Contains("HEX-001", violations[0].RuleBroken);
        Assert.Equal("s1", violations[0].SourceSymbolId);
        Assert.Equal("s2", violations[0].TargetSymbolId);
    }

    [Fact]
    public void Validate_MayOnlyReference_CatchesViolation()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "s1", Name = "AppService", QualifiedName = "App.Application.AppService", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "s2", Name = "HttpClient", QualifiedName = "App.Infrastructure.HttpClient", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "s1", TargetId = "s2", Kind = EdgeKind.DependsOn })
            .Build();

        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "HEX-002",
                Source = "App.Application",
                MayOnlyReference = "App.Domain",
            },
        };

        var violations = RuleValidator.Validate(graph, rules);
        Assert.Single(violations);
        Assert.Contains("HEX-002", violations[0].RuleBroken);
    }

    [Fact]
    public void Validate_SkipsContainsEdges()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Domain", Name = "Domain", QualifiedName = "App.Domain", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:Svc", Name = "Svc", QualifiedName = "App.Domain.Svc", Kind = SymbolKind.Type, ParentId = "mod:Domain" })
            .Build();

        // Contains edge from GraphBuilder. A rule that forbids Domain→Domain.Svc should NOT fire on Contains.
        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "BAD-RULE",
                Source = "App.Domain",
                MustNotReference = "App.Domain.Svc",
            },
        };

        var violations = RuleValidator.Validate(graph, rules);
        Assert.Empty(violations);
    }

    [Fact]
    public void Validate_WildcardPattern_MatchesPrefix()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "s1", Name = "X", QualifiedName = "Foo.Bar.X", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "s2", Name = "Y", QualifiedName = "Baz.Qux.Y", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "s1", TargetId = "s2", Kind = EdgeKind.References })
            .Build();

        var rules = new[]
        {
            new ArchitectureRule
            {
                Id = "WILD-001",
                Source = "Foo.*",
                MustNotReference = "Baz.*",
            },
        };

        var violations = RuleValidator.Validate(graph, rules);
        Assert.Single(violations);
    }

    [Fact]
    public void Validate_DoesNotMutateGraph()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "s1", Name = "A", QualifiedName = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "s2", Name = "B", QualifiedName = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "s1", TargetId = "s2", Kind = EdgeKind.References })
            .Build();

        int edgeCountBefore = graph.Edges.Length;
        int symbolCountBefore = graph.Symbols.Length;

        RuleValidator.Validate(graph, new[]
        {
            new ArchitectureRule { Id = "R1", Source = "A", MustNotReference = "B" },
        });

        Assert.Equal(edgeCountBefore, graph.Edges.Length);
        Assert.Equal(symbolCountBefore, graph.Symbols.Length);
    }

    /// <summary>
    /// A small compliant hexagonal graph: Domain → (nothing), Application → Domain.
    /// </summary>
    private static SemanticGraph MakeHexagonalGraph()
    {
        return new GraphBuilder()
            .AddSymbol(new Symbol { Id = "s1", Name = "Entity", QualifiedName = "App.Domain.Entity", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "s2", Name = "UseCase", QualifiedName = "App.Application.UseCase", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "s2", TargetId = "s1", Kind = EdgeKind.DependsOn })
            .Build();
    }
}
