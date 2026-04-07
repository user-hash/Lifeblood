using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

public class GraphValidatorTests
{
    // Helper: create raw graphs for validator testing (bypasses GraphBuilder normalization)
    private static SemanticGraph Raw(Symbol[]? symbols = null, Edge[]? edges = null)
        => new(symbols ?? Array.Empty<Symbol>(), edges ?? Array.Empty<Edge>());

    [Fact]
    public void Validate_EmptyGraph_NoErrors()
    {
        var errors = GraphValidator.Validate(new SemanticGraph());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ValidGraph_NoErrors()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.References })
            .Build();

        Assert.Empty(GraphValidator.Validate(graph));
    }

    [Fact]
    public void Validate_EmptySymbolId_Detected()
    {
        var graph = Raw(symbols: new[] { new Symbol { Id = "", Name = "Bad" } });
        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("EMPTY_SYMBOL_ID", errors[0].Code);
    }

    [Fact]
    public void Validate_DuplicateSymbolId_Detected()
    {
        var graph = Raw(symbols: new[]
        {
            new Symbol { Id = "type:Dup", Name = "Dup1", Kind = SymbolKind.Type },
            new Symbol { Id = "type:Dup", Name = "Dup2", Kind = SymbolKind.Type },
        });

        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("DUPLICATE_SYMBOL_ID", errors[0].Code);
    }

    [Fact]
    public void Validate_EmptySymbolName_Detected()
    {
        var graph = Raw(symbols: new[] { new Symbol { Id = "type:X", Name = "" } });
        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("EMPTY_SYMBOL_NAME", errors[0].Code);
    }

    [Fact]
    public void Validate_DanglingEdgeSource_Detected()
    {
        var graph = Raw(
            symbols: new[] { new Symbol { Id = "type:A", Name = "A" } },
            edges: new[] { new Edge { SourceId = "type:Missing", TargetId = "type:A", Kind = EdgeKind.Calls } });

        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("DANGLING_EDGE_SOURCE", errors[0].Code);
    }

    [Fact]
    public void Validate_DanglingEdgeTarget_Detected()
    {
        var graph = Raw(
            symbols: new[] { new Symbol { Id = "type:A", Name = "A" } },
            edges: new[] { new Edge { SourceId = "type:A", TargetId = "type:Ghost", Kind = EdgeKind.References } });

        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("DANGLING_EDGE_TARGET", errors[0].Code);
    }

    [Fact]
    public void Validate_SelfReferencingEdge_Detected()
    {
        var graph = Raw(
            symbols: new[] { new Symbol { Id = "type:Self", Name = "Self" } },
            edges: new[] { new Edge { SourceId = "type:Self", TargetId = "type:Self", Kind = EdgeKind.DependsOn } });

        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("SELF_REFERENCING_EDGE", errors[0].Code);
    }

    [Fact]
    public void Validate_DanglingParentId_Detected()
    {
        var graph = Raw(symbols: new[] { new Symbol { Id = "type:Child", Name = "Child", ParentId = "mod:Gone" } });
        var errors = GraphValidator.Validate(graph);
        Assert.Single(errors);
        Assert.Equal("DANGLING_PARENT_ID", errors[0].Code);
        Assert.Equal("type:Child", errors[0].SymbolId);
    }

    [Fact]
    public void Validate_MultipleErrors_AllReported()
    {
        var graph = Raw(
            symbols: new[]
            {
                new Symbol { Id = "", Name = "NoId" },
                new Symbol { Id = "type:A", Name = "" },
            },
            edges: new[] { new Edge { SourceId = "type:X", TargetId = "type:Y", Kind = EdgeKind.Calls } });

        var errors = GraphValidator.Validate(graph);
        Assert.True(errors.Length >= 4);
    }

    [Fact]
    public void Validate_GraphFromBuilder_IsValid()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Core", Name = "Core", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, ParentId = "mod:Core" })
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = SymbolKind.Type, ParentId = "file:A.cs" })
            .AddSymbol(new Symbol { Id = "method:A.Do", Name = "Do", Kind = SymbolKind.Method, ParentId = "type:A" })
            .Build();

        Assert.Empty(GraphValidator.Validate(graph));
    }
}
