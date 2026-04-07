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
            Summary = new GraphSummary
            {
                TotalSymbols = graph.Symbols.Count,
                TotalEdges = graph.Edges.Count,
                Modules = graph.Symbols.Count(s => s.Kind == SymbolKind.Module),
                Types = graph.Symbols.Count(s => s.Kind == SymbolKind.Type),
                Methods = graph.Symbols.Count(s => s.Kind == SymbolKind.Method),
                Files = graph.Symbols.Count(s => s.Kind == SymbolKind.File),
                Cycles = analysis.Cycles.Length,
                Violations = analysis.Violations.Length,
            },
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

        // Only report module-level purity — that's architecturally significant.
        // Leaf types being "pure" is trivially obvious and adds noise.
        foreach (var tier in analysis.Tiers)
        {
            if (tier.Tier == ArchitectureTier.Pure && tier.SymbolId.StartsWith("mod:"))
                invariants.Add($"{tier.SymbolId} is pure (zero dependencies)");
        }

        if (analysis.Cycles.Length > 0)
            invariants.Add($"{analysis.Cycles.Length} circular dependency cycles detected");

        if (analysis.Violations.Length == 0)
            invariants.Add("No architecture violations detected");
        else
            invariants.Add($"{analysis.Violations.Length} architecture violations found");

        return invariants.ToArray();
    }

    private static string[] IdentifyHotspots(Dictionary<string, CouplingMetrics> coupling)
    {
        // Exclude modules — composition roots and test projects have high coupling by design.
        // Hotspots should surface type-level problems, not structural roles.
        return coupling.Values
            .Where(c => !c.SymbolId.StartsWith("mod:"))
            .Where(c => c.FanIn + c.FanOut > 5 && c.Instability > 0.5f)
            .OrderByDescending(c => c.FanIn + c.FanOut)
            .Take(20)
            .Select(c => c.SymbolId)
            .ToArray();
    }

    private static ModuleDependency[] BuildDependencyMatrix(SemanticGraph graph)
    {
        var matrix = new List<ModuleDependency>();

        // Use module-level DependsOn edges (reliable, from csproj parsing).
        // Cross-module type-level edges depend on compilation fidelity
        // and may be incomplete — module-level is always truthful.
        foreach (var symbol in graph.Symbols)
        {
            if (symbol.Kind != SymbolKind.Module) continue;

            // Count type-level cross-module edges where available
            var memberIds = CollectModuleMembers(graph, symbol.Id);

            foreach (int idx in graph.GetOutgoingEdgeIndexes(symbol.Id))
            {
                var edge = graph.Edges[idx];
                if (edge.Kind != EdgeKind.DependsOn) continue;

                var target = graph.GetSymbol(edge.TargetId);
                if (target == null) continue;

                var targetMembers = CollectModuleMembers(graph, target.Id);

                // Count how many non-Contains edges cross from our members to their members
                int crossEdgeCount = 0;
                foreach (var memberId in memberIds)
                {
                    foreach (int eidx in graph.GetOutgoingEdgeIndexes(memberId))
                    {
                        var e = graph.Edges[eidx];
                        if (e.Kind == EdgeKind.Contains) continue;
                        if (targetMembers.Contains(e.TargetId))
                            crossEdgeCount++;
                    }
                }

                matrix.Add(new ModuleDependency
                {
                    Source = symbol.Name,
                    Target = target.Name,
                    // Use cross-edge count if available, otherwise 1 (the module DependsOn edge itself)
                    EdgeCount = crossEdgeCount > 0 ? crossEdgeCount : 1,
                });
            }
        }

        return matrix.ToArray();
    }

    private static HashSet<string> CollectModuleMembers(SemanticGraph graph, string moduleId)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(moduleId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in graph.ChildrenOf(current))
            {
                if (members.Add(child.Id))
                    queue.Enqueue(child.Id);
            }
        }

        return members;
    }
}
