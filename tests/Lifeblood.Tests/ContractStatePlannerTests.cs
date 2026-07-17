using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class ContractStatePlannerTests
{
    [Fact]
    public void Plan_ProjectsCanonicalMutabilityMetadataWithDeterministicBounds()
    {
        const string root = "method:Acme.Root.Run()";
        var graph = new GraphBuilder()
            .AddSymbol(State(root, SymbolKind.Method, isStatic: true))
            .AddSymbol(State("field:Acme.State.Cache", SymbolKind.Field, isStatic: true,
                (SymbolPropertyKeys.FieldType, "int"),
                (SymbolPropertyKeys.HasInitializer, "true")))
            .AddSymbol(State("field:Acme.State.Table", SymbolKind.Field, isStatic: true,
                (SymbolPropertyKeys.FieldType, "float[]"),
                (SymbolPropertyKeys.IsReadOnly, "true"),
                (SymbolPropertyKeys.HasInitializer, "true")))
            .AddSymbol(State("property:Acme.State.Value", SymbolKind.Property, isStatic: true,
                (SymbolPropertyKeys.PropertyType, "int"),
                (SymbolPropertyKeys.HasSetter, "true")))
            .AddSymbol(State("field:Acme.State.Instance", SymbolKind.Field, isStatic: false,
                (SymbolPropertyKeys.FieldType, "int")))
            .AddSymbol(State("field:Acme.State.Aardvark", SymbolKind.Field, isStatic: true,
                (SymbolPropertyKeys.FieldType, "int")))
            .AddEdge(Reference(root, "field:Acme.State.Cache", "Player"))
            .AddEdge(Reference(root, "field:Acme.State.Table", "Player"))
            .AddEdge(Reference(root, "property:Acme.State.Value", "Player"))
            .AddEdge(Reference(root, "field:Acme.State.Instance", "Player"))
            .AddEdge(Reference(root, "field:Acme.State.Aardvark", "Editor"))
            .Build();
        var contract = Contract(maxMembers: 2);
        var routes = ContractCallRoutePlanner.Plan(
            graph,
            new[] { new ContractCallRoute { Id = "route", RootSymbolIds = new[] { root } } },
            "Player");

        var first = ContractStatePlanner.Plan(graph, new[] { contract }, routes, "Player");
        var second = ContractStatePlanner.Plan(graph, new[] { contract }, routes, "Player");

        Assert.Equal(first.Members.Select(member => member.SymbolId), second.Members.Select(member => member.SymbolId));
        Assert.Equal(
            new[] { "field:Acme.State.Cache", "field:Acme.State.Table" },
            first.Members.Select(member => member.SymbolId));
        var receipt = Assert.Single(first.Contracts);
        Assert.Equal(3, receipt.CandidateMemberCount);
        Assert.Equal(2, receipt.RetainedMemberCount);
        Assert.True(receipt.Truncated);
        Assert.True(first.Members[0].HasInitializer);
        Assert.True(first.Members[1].IsReadOnly);
        Assert.Equal("float[]", first.Members[1].ValueType);
    }

    [Fact]
    public void Plan_RejectsMissingWrongKindAndOutOfScopeExactTargets()
    {
        var graph = new GraphBuilder()
            .AddSymbol(State("field:Acme.State.Instance", SymbolKind.Field, isStatic: false,
                (SymbolPropertyKeys.FieldType, "int")))
            .AddSymbol(State("field:Acme.State.Constant", SymbolKind.Field, isStatic: true,
                (SymbolPropertyKeys.FieldType, "int"),
                (SymbolPropertyKeys.IsConst, "true")))
            .AddSymbol(State("method:Acme.State.Run()", SymbolKind.Method, isStatic: true))
            .Build();

        Assert.Contains("not present", Assert.Throws<ArgumentException>(() => ContractStatePlanner.Plan(
            graph,
            new[] { Exact("field:Acme.State.Missing") },
            ContractCallRoutePlan.Empty,
            null)).Message);
        Assert.Contains("must be fields or properties", Assert.Throws<ArgumentException>(() => ContractStatePlanner.Plan(
            graph,
            new[] { Exact("method:Acme.State.Run()") },
            ContractCallRoutePlan.Empty,
            null)).Message);
        Assert.Contains("outside memberScope", Assert.Throws<ArgumentException>(() => ContractStatePlanner.Plan(
            graph,
            new[] { Exact("field:Acme.State.Instance") },
            ContractCallRoutePlan.Empty,
            null)).Message);
        Assert.Contains("non-constant", Assert.Throws<ArgumentException>(() => ContractStatePlanner.Plan(
            graph,
            new[] { Exact("field:Acme.State.Constant") },
            ContractCallRoutePlan.Empty,
            null)).Message);
    }

    [Fact]
    public void ManifestValidation_RequiresExplicitMemberModeKnownRoutesAndKnownRiskBuckets()
    {
        var route = new ContractCallRoute { Id = "route", RootSymbolIds = new[] { "method:Acme.Run()" } };
        StateAccessContract State(bool matchAny, string[] targets, string[] routes, string[] buckets)
            => new()
            {
                Id = "state",
                MatchAnyMember = matchAny,
                TargetSymbolIds = targets,
                CallRouteIds = routes,
                AllowedRiskBuckets = buckets,
                Categories = new[] { "SharedState" },
            };
        ContractManifest Manifest(StateAccessContract state)
            => new() { Id = "policy", Version = "1", CallRoutes = new[] { route }, StateAccesses = new[] { state } };

        Assert.Contains("exactly one", Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(Manifest(
            State(true, new[] { "field:Acme.State.Value" }, new[] { "route" }, new[] { StateRiskBucket.ReadonlyTable })))).Message);
        Assert.Contains("unknown call routes", Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(Manifest(
            State(true, Array.Empty<string>(), new[] { "missing" }, new[] { StateRiskBucket.ReadonlyTable })))).Message);
        Assert.Contains("unknown allowedRiskBuckets", Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(Manifest(
            State(true, Array.Empty<string>(), new[] { "route" }, new[] { "ProbablySafe" })))).Message);
    }

    private static StateAccessContract Contract(int maxMembers)
        => new()
        {
            Id = "state",
            MatchAnyMember = true,
            CallRouteIds = new[] { "route" },
            Categories = new[] { "SharedState" },
            MaxMembers = maxMembers,
        };

    private static StateAccessContract Exact(string symbolId)
        => new()
        {
            Id = "state",
            TargetSymbolIds = new[] { symbolId },
            CallRouteIds = new[] { "route" },
            Categories = new[] { "SharedState" },
        };

    private static Edge Reference(string sourceId, string targetId, params string[] profiles)
        => new()
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = EdgeKind.References,
            Profiles = profiles.Length == 0 ? null : profiles,
        };

    private static Symbol State(
        string id,
        SymbolKind kind,
        bool isStatic,
        params (string Key, string Value)[] properties)
        => new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            IsStatic = isStatic,
            FilePath = "State.cs",
            Line = 3,
            Properties = properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
}
