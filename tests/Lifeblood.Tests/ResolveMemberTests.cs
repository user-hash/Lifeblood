using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Stage 2.A — pins the <see cref="ISymbolResolver.ResolveMember"/> contract.
/// Field-report P1 ask (2026-05-11): type-scoped member lookup with optional
/// overload disambiguation by param signature. Covers every outcome in
/// <see cref="ResolveMemberOutcome"/> plus the three input shapes (canonical
/// type id, fully-qualified name, short name).
/// </summary>
public class ResolveMemberTests
{
    private static readonly LifebloodSymbolResolver Resolver = new();

    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    private static Symbol Type(string id, string name) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("type:") ? id["type:".Length..] : id,
        Kind = SymbolKind.Type,
        FilePath = "test.cs",
    };

    private static Symbol Method(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("method:") ? id["method:".Length..] : id,
        Kind = SymbolKind.Method,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static Symbol Property(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("property:") ? id["property:".Length..] : id,
        Kind = SymbolKind.Property,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static Symbol Field(string id, string name, string parentId) => new()
    {
        Id = id,
        Name = name,
        QualifiedName = id.StartsWith("field:") ? id["field:".Length..] : id,
        Kind = SymbolKind.Field,
        ParentId = parentId,
        FilePath = "test.cs",
    };

    private static SemanticGraph BuildGraph(params (Symbol? sym, Edge? edge)[] entries)
    {
        var b = new GraphBuilder();
        foreach (var (sym, edge) in entries)
        {
            if (sym != null) b.AddSymbol(sym);
            if (edge != null) b.AddEdge(edge);
        }
        return b.Build();
    }

    private static Edge Contains(string source, string target) => new()
    {
        SourceId = source,
        TargetId = target,
        Kind = EdgeKind.Contains,
        Evidence = Ev,
    };

    [Fact]
    public void ResolveMember_CanonicalTypeId_FindsSingleMethodOverload()
    {
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.Do(int)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(int)")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "Do");

        Assert.Equal(ResolveMemberOutcome.Unique, r.Outcome);
        Assert.Single(r.Members);
        Assert.Equal("method:NS.Svc.Do(int)", r.Members[0].CanonicalId);
        Assert.Equal(SymbolKind.Method, r.Members[0].Kind);
        Assert.Equal("int", r.Members[0].ParamDisplay);
        Assert.Equal("type:NS.Svc", r.ResolvedTypeId);
    }

    [Fact]
    public void ResolveMember_MultipleOverloads_ReturnsMultipleMatches()
    {
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.Do(int)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(int)")),
            (Method("method:NS.Svc.Do(string)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(string)")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "Do");

        Assert.Equal(ResolveMemberOutcome.MultipleMatches, r.Outcome);
        Assert.Equal(2, r.Members.Length);
    }

    [Fact]
    public void ResolveMember_ParamTypeFilter_DisambiguatesOverload()
    {
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.Do(int)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(int)")),
            (Method("method:NS.Svc.Do(string)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(string)")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "Do", new[] { "string" });

        Assert.Equal(ResolveMemberOutcome.Unique, r.Outcome);
        Assert.Single(r.Members);
        Assert.Equal("method:NS.Svc.Do(string)", r.Members[0].CanonicalId);
    }

    [Fact]
    public void ResolveMember_MixedKindMembers_ReturnsAllNamedMatches()
    {
        // Type carries both a method AND a property with the same name (overload-like across kinds).
        // ResolveMember does not discriminate on kind — caller gets both back.
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.Name(int)", "Name", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Name(int)")),
            (Property("property:NS.Svc.Name", "Name", "type:NS.Svc"),
                Contains("type:NS.Svc", "property:NS.Svc.Name")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "Name");

        Assert.Equal(ResolveMemberOutcome.MultipleMatches, r.Outcome);
        Assert.Equal(2, r.Members.Length);
        Assert.Contains(r.Members, m => m.Kind == SymbolKind.Method);
        Assert.Contains(r.Members, m => m.Kind == SymbolKind.Property);
    }

    [Fact]
    public void ResolveMember_BareShortName_UniqueTypeResolves()
    {
        var graph = BuildGraph(
            (Type("type:NS.Foo", "Foo"), null),
            (Field("field:NS.Foo.X", "X", "type:NS.Foo"),
                Contains("type:NS.Foo", "field:NS.Foo.X")));

        var r = Resolver.ResolveMember(graph, "Foo", "X");

        Assert.Equal(ResolveMemberOutcome.Unique, r.Outcome);
        Assert.Single(r.Members);
        Assert.Equal("field:NS.Foo.X", r.Members[0].CanonicalId);
        Assert.Equal(SymbolKind.Field, r.Members[0].Kind);
        Assert.Equal("type:NS.Foo", r.ResolvedTypeId);
    }

    [Fact]
    public void ResolveMember_BareShortName_AmbiguousTypeReturnsCandidates()
    {
        var graph = BuildGraph(
            (Type("type:NS1.Foo", "Foo"), null),
            (Type("type:NS2.Foo", "Foo"), null),
            (Field("field:NS1.Foo.X", "X", "type:NS1.Foo"),
                Contains("type:NS1.Foo", "field:NS1.Foo.X")),
            (Field("field:NS2.Foo.X", "X", "type:NS2.Foo"),
                Contains("type:NS2.Foo", "field:NS2.Foo.X")));

        var r = Resolver.ResolveMember(graph, "Foo", "X");

        Assert.Equal(ResolveMemberOutcome.AmbiguousContainingType, r.Outcome);
        Assert.Equal(2, r.AmbiguousTypeCandidates.Length);
        Assert.Empty(r.Members);
        Assert.Null(r.ResolvedTypeId);
    }

    [Fact]
    public void ResolveMember_FullyQualifiedTypeName_Resolves()
    {
        var graph = BuildGraph(
            (Type("type:NS.Foo", "Foo"), null),
            (Method("method:NS.Foo.Bar()", "Bar", "type:NS.Foo"),
                Contains("type:NS.Foo", "method:NS.Foo.Bar()")));

        var r = Resolver.ResolveMember(graph, "NS.Foo", "Bar");

        Assert.Equal(ResolveMemberOutcome.Unique, r.Outcome);
        Assert.Equal("type:NS.Foo", r.ResolvedTypeId);
    }

    [Fact]
    public void ResolveMember_TypeNotFound_ReturnsTypeNotFound()
    {
        var graph = BuildGraph((Type("type:NS.Real", "Real"), null));

        var r = Resolver.ResolveMember(graph, "type:NS.Nonexistent", "X");

        Assert.Equal(ResolveMemberOutcome.TypeNotFound, r.Outcome);
        Assert.Null(r.ResolvedTypeId);
        Assert.Empty(r.Members);
    }

    [Fact]
    public void ResolveMember_MemberNotFound_ReturnsNotFoundWithResolvedTypeId()
    {
        var graph = BuildGraph((Type("type:NS.Foo", "Foo"), null));

        var r = Resolver.ResolveMember(graph, "type:NS.Foo", "Missing");

        Assert.Equal(ResolveMemberOutcome.NotFound, r.Outcome);
        Assert.Equal("type:NS.Foo", r.ResolvedTypeId);
        Assert.Empty(r.Members);
    }

    [Fact]
    public void ResolveMember_ParamFilterMissesEveryOverload_ReturnsNotFound()
    {
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.Do(int)", "Do", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.Do(int)")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "Do", new[] { "double" });

        Assert.Equal(ResolveMemberOutcome.NotFound, r.Outcome);
        Assert.Contains("double", r.Diagnostic);
    }

    [Fact]
    public void ResolveMember_NonMethodMembers_ParamFilterIgnored()
    {
        // ParamTypeFilter applies only to methods. A property + method with the
        // same name, filter for "int" → keeps the method that matches AND the
        // property (filter doesn't apply to props).
        var graph = BuildGraph(
            (Type("type:NS.Svc", "Svc"), null),
            (Method("method:NS.Svc.X(int)", "X", "type:NS.Svc"),
                Contains("type:NS.Svc", "method:NS.Svc.X(int)")),
            (Property("property:NS.Svc.X", "X", "type:NS.Svc"),
                Contains("type:NS.Svc", "property:NS.Svc.X")));

        var r = Resolver.ResolveMember(graph, "type:NS.Svc", "X", new[] { "int" });

        Assert.Equal(ResolveMemberOutcome.MultipleMatches, r.Outcome);
        Assert.Equal(2, r.Members.Length);
    }

    [Fact]
    public void ResolveMember_EmptyInput_ReturnsTypeNotFound()
    {
        var graph = BuildGraph();

        Assert.Equal(ResolveMemberOutcome.TypeNotFound,
            Resolver.ResolveMember(graph, "", "X").Outcome);
        Assert.Equal(ResolveMemberOutcome.TypeNotFound,
            Resolver.ResolveMember(graph, "type:NS.Foo", "").Outcome);
    }
}
