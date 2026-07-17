using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

public sealed class ContractCallRoutePlannerTests
{
    private const string Root = "method:Acme.Root.Run()";
    private const string Direct = "method:Acme.Work.Direct()";
    private const string Transitive = "method:Acme.Work.Transitive()";
    private const string EditorOnly = "method:Acme.Editor.Run()";

    [Fact]
    public void Plan_UsesShortestProfileApplicableCallPathsAndStopsCycles()
    {
        var graph = Graph(
            Call(Root, Direct, "Editor", "Player"),
            Call(Direct, Transitive, "Player"),
            Call(Root, EditorOnly, "Editor"),
            Call(Transitive, Root, "Player"));
        var route = Route(maxDepth: 8, maxMembers: 16);

        var first = ContractCallRoutePlanner.Plan(graph, new[] { route }, "Player");
        var second = ContractCallRoutePlanner.Plan(graph, new[] { route }, "Player");

        Assert.Equal(
            first.Matches.Select(Identity),
            second.Matches.Select(Identity));
        Assert.Equal(new[] { Root, Direct, Transitive }, first.Matches.Select(match => match.ContainingSymbolId));
        Assert.Equal(new[] { 0, 1, 2 }, first.Matches.Select(match => match.Distance));
        Assert.Equal(ContractCallRoutePlacement.Direct, first.Matches[0].Placement);
        Assert.Equal(ContractCallRoutePlacement.Transitive, first.Matches[2].Placement);
        Assert.Equal(new[] { Root, Direct, Transitive }, first.Matches[2].PathSymbolIds);

        var receipt = Assert.Single(first.Routes);
        Assert.Equal(3, receipt.ReachableMemberCount);
        Assert.Equal(3, receipt.MembershipCount);
        Assert.False(receipt.Truncated);

        var editor = ContractCallRoutePlanner.Plan(graph, new[] { route }, "Editor");
        Assert.Equal(new[] { Root, EditorOnly, Direct }, editor.Matches.Select(match => match.ContainingSymbolId));
    }

    [Fact]
    public void Plan_EnforcesMemberBoundWithDeterministicRetainedMembership()
    {
        const string alpha = "method:Acme.Work.Alpha()";
        const string beta = "method:Acme.Work.Beta()";
        var graph = Graph(Call(Root, beta), Call(Root, alpha));

        var plan = ContractCallRoutePlanner.Plan(
            graph,
            new[] { Route(maxDepth: 2, maxMembers: 2) },
            profileScope: null);

        Assert.Equal(new[] { Root, alpha }, plan.Matches.Select(match => match.ContainingSymbolId));
        Assert.True(Assert.Single(plan.Routes).Truncated);
    }

    [Fact]
    public void Plan_RejectsMissingAndNonMethodRoots()
    {
        const string typeId = "type:Acme.Root";
        var graph = new GraphBuilder()
            .AddSymbol(Method(Root))
            .AddSymbol(new Symbol { Id = typeId, Name = "Root", Kind = SymbolKind.Type })
            .Build();

        var missing = Assert.Throws<ArgumentException>(() => ContractCallRoutePlanner.Plan(
            graph,
            new[] { new ContractCallRoute { Id = "route", RootSymbolIds = new[] { "method:Missing.Run()" } } },
            "Player"));
        Assert.Contains("not present", missing.Message);

        var wrongKind = Assert.Throws<ArgumentException>(() => ContractCallRoutePlanner.Plan(
            graph,
            new[] { new ContractCallRoute { Id = "route", RootSymbolIds = new[] { typeId } } },
            "Player"));
        Assert.Contains("must be methods", wrongKind.Message);
    }

    [Fact]
    public void ManifestValidation_RejectsUnknownRouteReferencesAndBoundsThatDropRoots()
    {
        var unknownRoute = new ContractManifest
        {
            Id = "policy",
            Version = "1",
            ExternalApiCosts = new[]
            {
                new ExternalApiCostContract
                {
                    Id = "cost",
                    TargetSymbolIds = new[] { "method:Acme.Vendor.Allocate()" },
                    CallRouteIds = new[] { "missing-route" },
                    Categories = new[] { "Allocation" },
                    AnnotationSource = "policy",
                },
            },
        };
        var unknown = Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(unknownRoute));
        Assert.Contains("unknown call routes", unknown.Message);

        var dropsRoot = new ContractManifest
        {
            Id = "policy",
            Version = "1",
            CallRoutes = new[]
            {
                new ContractCallRoute
                {
                    Id = "route",
                    RootSymbolIds = new[] { Root, Direct },
                    MaxMembers = 1,
                },
            },
        };
        var bound = Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(dropsRoot));
        Assert.Contains("must retain all 2 declared roots", bound.Message);

        ExternalApiCostContract Cost(bool matchAnyTarget, params string[] targetSymbolIds)
            => new()
            {
                Id = "cost",
                TargetSymbolIds = targetSymbolIds,
                MatchAnyTarget = matchAnyTarget,
                ReportEveryOccurrence = true,
                Categories = new[] { "Allocation" },
                AnnotationSource = "policy",
            };
        var bothSelectors = new ContractManifest
        {
            Id = "policy",
            Version = "1",
            ExternalApiCosts = new[] { Cost(true, "method:Acme.Vendor.Allocate()") },
        };
        Assert.Contains(
            "exactly one",
            Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(bothSelectors)).Message);
        var noSelector = new ContractManifest
        {
            Id = "policy",
            Version = "1",
            ExternalApiCosts = new[] { Cost(false) },
        };
        Assert.Contains(
            "exactly one",
            Assert.Throws<ArgumentException>(() => ContractManifestValidator.Validate(noSelector)).Message);
    }

    private static ContractCallRoute Route(int maxDepth, int maxMembers)
        => new()
        {
            Id = "audio-route",
            RootSymbolIds = new[] { Root },
            MaxDepth = maxDepth,
            MaxMembers = maxMembers,
        };

    private static SemanticGraph Graph(params Edge[] edges)
    {
        var ids = edges
            .SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
            .Append(Root)
            .Distinct(StringComparer.Ordinal);
        return new GraphBuilder()
            .AddSymbols(ids.Select(Method))
            .AddEdges(edges)
            .Build();
    }

    private static Symbol Method(string id)
        => new()
        {
            Id = id,
            Name = id[(id.LastIndexOf('.') + 1)..],
            Kind = SymbolKind.Method,
        };

    private static Edge Call(string source, string target, params string[] profiles)
        => new()
        {
            SourceId = source,
            TargetId = target,
            Kind = EdgeKind.Calls,
            Profiles = profiles.Length == 0 ? null : profiles,
        };

    private static string Identity(ContractCallRouteMatch match)
        => $"{match.RouteId}|{match.RootSymbolId}|{match.ContainingSymbolId}|{match.Distance}|" +
           string.Join(">", match.PathSymbolIds);
}
