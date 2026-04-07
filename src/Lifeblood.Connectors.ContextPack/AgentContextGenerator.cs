using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Connectors.ContextPack;

/// <summary>
/// THE KILLER FEATURE. Graph + analysis → AI-consumable context pack.
/// INV-CONN-001: Depends on Application ports, not on adapters.
/// INV-CONN-003: Produces AI-consumable JSON, not human prose.
/// </summary>
public sealed class AgentContextGenerator : IAgentContextGenerator
{
    public AgentContextPack Generate(SemanticGraph graph, AnalysisResult analysis)
    {
        var couplingById = analysis.Coupling
            .ToDictionary(c => c.SymbolId, StringComparer.Ordinal);

        var tierById = analysis.Tiers
            .ToDictionary(t => t.SymbolId, StringComparer.Ordinal);

        return new AgentContextPack
        {
            HighValueFiles = IdentifyHighValueFiles(graph, couplingById, tierById),
            Boundaries = IdentifyBoundaries(graph, tierById),
            Invariants = ExtractInvariants(analysis),
            Hotspots = IdentifyHotspots(couplingById),
            ReadingOrder = ReadingOrderGenerator.Generate(graph, analysis.Coupling),
            DependencyMatrix = BuildDependencyMatrix(graph),
            ActiveViolations = analysis.Violations.Select(v => v.RuleBroken).Distinct().ToArray(),
        };
    }

    private static HighValueFile[] IdentifyHighValueFiles(
        SemanticGraph graph,
        Dictionary<string, CouplingMetrics> coupling,
        Dictionary<string, TierAssignment> tiers)
    {
        var files = new List<HighValueFile>();

        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.File) continue;
            if (string.IsNullOrEmpty(symbol.FilePath)) continue;

            // Aggregate fan-in and instability from children
            int totalFanIn = 0;
            float maxInstability = 0;
            string tier = "";

            foreach (var child in graph.ChildrenOf(symbol.Id))
            {
                if (coupling.TryGetValue(child.Id, out var m))
                {
                    totalFanIn += m.FanIn;
                    if (m.Instability > maxInstability) maxInstability = m.Instability;
                }
                if (tier == "" && tiers.TryGetValue(child.Id, out var t))
                    tier = t.Tier.ToString();
            }

            if (totalFanIn > 0)
            {
                files.Add(new HighValueFile
                {
                    FilePath = symbol.FilePath,
                    FanIn = totalFanIn,
                    Instability = maxInstability,
                    Tier = tier,
                });
            }
        }

        return files.OrderByDescending(f => f.FanIn).Take(50).ToArray();
    }

    private static BoundaryInfo[] IdentifyBoundaries(
        SemanticGraph graph,
        Dictionary<string, TierAssignment> tiers)
    {
        var boundaries = new List<BoundaryInfo>();

        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.Module) continue;

            var deps = new List<string>();
            foreach (int idx in graph.GetOutgoingEdgeIndexes(symbol.Id))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind == EdgeKind.DependsOn)
                {
                    var target = graph.GetSymbol(edge.TargetId);
                    if (target != null) deps.Add(target.Name);
                }
            }

            tiers.TryGetValue(symbol.Id, out var tier);

            boundaries.Add(new BoundaryInfo
            {
                ModuleName = symbol.Name,
                Tier = tier?.Tier.ToString() ?? "",
                DependsOn = deps.ToArray(),
                IsPure = deps.Count == 0,
            });
        }

        return boundaries.ToArray();
    }

    private static string[] ExtractInvariants(AnalysisResult analysis)
    {
        var invariants = new List<string>();

        foreach (var tier in analysis.Tiers)
        {
            if (tier.Tier == ArchitectureTier.Pure)
                invariants.Add($"{tier.SymbolId} is pure (zero dependencies)");
        }

        if (analysis.Violations.Length == 0)
            invariants.Add("No architecture violations detected");
        else
            invariants.Add($"{analysis.Violations.Length} architecture violations found");

        return invariants.ToArray();
    }

    private static string[] IdentifyHotspots(Dictionary<string, CouplingMetrics> coupling)
    {
        return coupling.Values
            .Where(c => c.FanIn + c.FanOut > 5 && c.Instability > 0.5f)
            .OrderByDescending(c => c.FanIn + c.FanOut)
            .Take(20)
            .Select(c => c.SymbolId)
            .ToArray();
    }

    private static ModuleDependency[] BuildDependencyMatrix(SemanticGraph graph)
    {
        // Build module membership: symbolId → moduleId
        var memberOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind == SymbolKind.Module) continue;
            // Walk up parentId chain to find module
            var current = symbol;
            while (current != null && current.Kind != SymbolKind.Module)
            {
                if (string.IsNullOrEmpty(current.ParentId)) break;
                current = graph.GetSymbol(current.ParentId);
            }
            if (current?.Kind == SymbolKind.Module)
                memberOf[symbol.Id] = current.Id;
        }

        // Count cross-module edges
        var crossEdges = new Dictionary<(string src, string tgt), int>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == EdgeKind.Contains) continue;
            if (!memberOf.TryGetValue(edge.SourceId, out var srcMod)) continue;
            if (!memberOf.TryGetValue(edge.TargetId, out var tgtMod)) continue;
            if (srcMod == tgtMod) continue;

            var key = (srcMod, tgtMod);
            crossEdges[key] = crossEdges.GetValueOrDefault(key) + 1;
        }

        return crossEdges.Select(kv =>
        {
            var srcName = graph.GetSymbol(kv.Key.src)?.Name ?? kv.Key.src;
            var tgtName = graph.GetSymbol(kv.Key.tgt)?.Name ?? kv.Key.tgt;
            return new ModuleDependency
            {
                Source = srcName,
                Target = tgtName,
                EdgeCount = kv.Value,
            };
        }).ToArray();
    }
}
