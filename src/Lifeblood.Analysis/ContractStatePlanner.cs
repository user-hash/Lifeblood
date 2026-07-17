using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>
/// Projects declaration-level state metadata from the immutable graph for one
/// contract request. It is a bounded query plan, not a retained member index or
/// a second semantic source.
/// </summary>
public static class ContractStatePlanner
{
    public static ContractStatePlan Plan(
        SemanticGraph graph,
        IReadOnlyList<StateAccessContract> contracts,
        ContractCallRoutePlan callRoutePlan,
        string? profileScope)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(callRoutePlan);
        if (contracts.Count == 0) return ContractStatePlan.Empty;

        var receipts = new List<ContractStatePlanReceipt>(contracts.Count);
        var members = new List<ContractStateMember>();
        foreach (var contract in contracts.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            var candidates = SelectCandidates(graph, contract, callRoutePlan, profileScope);
            var retained = candidates.Take(contract.MaxMembers).ToArray();
            receipts.Add(new ContractStatePlanReceipt
            {
                ContractId = contract.Id,
                CandidateMemberCount = candidates.Length,
                RetainedMemberCount = retained.Length,
                MaxMembers = contract.MaxMembers,
                Truncated = candidates.Length > retained.Length,
            });
            members.AddRange(retained.Select(symbol => Project(contract.Id, symbol)));
        }

        return new ContractStatePlan
        {
            Contracts = receipts.ToArray(),
            Members = members
                .OrderBy(member => member.ContractId, StringComparer.Ordinal)
                .ThenBy(member => member.SymbolId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static Symbol[] SelectCandidates(
        SemanticGraph graph,
        StateAccessContract contract,
        ContractCallRoutePlan callRoutePlan,
        string? profileScope)
    {
        IEnumerable<Symbol> candidates;
        if (contract.MatchAnyMember)
        {
            var routeMemberIds = callRoutePlan.Matches
                .Where(match => contract.CallRouteIds.Contains(match.RouteId, StringComparer.Ordinal))
                .Select(match => match.ContainingSymbolId)
                .ToHashSet(StringComparer.Ordinal);
            var referencedStateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var routeMemberId in routeMemberIds)
            {
                foreach (var edgeIndex in graph.GetOutgoingEdgeIndexes(routeMemberId))
                {
                    var edge = graph.Edges[edgeIndex];
                    if (edge.Kind == EdgeKind.References
                        && ContractCallRoutePlanner.AppliesToProfile(edge, profileScope))
                    {
                        referencedStateIds.Add(edge.TargetId);
                    }
                }
            }
            candidates = referencedStateIds
                .Select(graph.GetSymbol)
                .Where(symbol => symbol != null)
                .Select(symbol => symbol!)
                .Where(IsEligibleStateMember);
        }
        else
        {
            var selected = new List<Symbol>(contract.TargetSymbolIds.Length);
            foreach (var symbolId in contract.TargetSymbolIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                var symbol = graph.GetSymbol(symbolId)
                    ?? throw new ArgumentException(
                        $"State-access contract '{contract.Id}' target '{symbolId}' is not present in the selected graph.");
                if (!IsStateMember(symbol))
                {
                    throw new ArgumentException(
                        $"State-access contract '{contract.Id}' target '{symbolId}' is {symbol.Kind}; " +
                        "state targets must be fields or properties.");
                }
                if (!IsEligibleStateMember(symbol))
                {
                    throw new ArgumentException(
                        $"State-access contract '{contract.Id}' target '{symbolId}' is constant declaration data; " +
                        "state targets must be non-constant fields or properties.");
                }
                selected.Add(symbol);
            }
            candidates = selected;
        }

        var retained = candidates
            .Where(symbol => ScopeMatches(contract.MemberScope, symbol.IsStatic))
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToArray();
        if (!contract.MatchAnyMember && retained.Length != contract.TargetSymbolIds.Length)
        {
            throw new ArgumentException(
                $"State-access contract '{contract.Id}' has an exact target outside memberScope '{contract.MemberScope}'.");
        }
        return retained;
    }

    private static ContractStateMember Project(string contractId, Symbol symbol)
        => new()
        {
            ContractId = contractId,
            SymbolId = symbol.Id,
            MemberKind = symbol.Kind.ToString(),
            ValueType = Property(symbol, symbol.Kind == SymbolKind.Field
                ? SymbolPropertyKeys.FieldType
                : SymbolPropertyKeys.PropertyType),
            IsStatic = symbol.IsStatic,
            IsReadOnly = BooleanProperty(symbol, SymbolPropertyKeys.IsReadOnly),
            IsConst = BooleanProperty(symbol, SymbolPropertyKeys.IsConst),
            HasSetter = BooleanProperty(symbol, SymbolPropertyKeys.HasSetter),
            HasInitializer = BooleanProperty(symbol, SymbolPropertyKeys.HasInitializer),
            DeclarationSource = new OperationSourceSpan
            {
                FilePath = symbol.FilePath,
                Line = Math.Max(1, symbol.Line),
                Column = 1,
                EndLine = Math.Max(1, symbol.Line),
                EndColumn = 1,
            },
        };

    private static bool IsStateMember(Symbol symbol)
        => symbol.Kind switch
        {
            SymbolKind.Field => symbol.Properties.ContainsKey(SymbolPropertyKeys.FieldType),
            SymbolKind.Property => symbol.Properties.ContainsKey(SymbolPropertyKeys.PropertyType),
            _ => false,
        };

    private static bool IsEligibleStateMember(Symbol symbol)
        => IsStateMember(symbol)
           && !BooleanProperty(symbol, SymbolPropertyKeys.IsConst)
           && !string.Equals(
               Property(symbol, SymbolPropertyKeys.FieldKind),
               "enumMember",
               StringComparison.Ordinal);

    private static bool ScopeMatches(string scope, bool isStatic)
        => scope switch
        {
            StateMemberScope.Static => isStatic,
            StateMemberScope.Instance => !isStatic,
            StateMemberScope.Any => true,
            _ => false,
        };

    private static string Property(Symbol symbol, string key)
        => symbol.Properties.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool BooleanProperty(Symbol symbol, string key)
        => symbol.Properties.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
}
