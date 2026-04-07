using Lifeblood.Core.Graph;
using Lifeblood.Core.Ports;

namespace Lifeblood.Core.Analysis;

/// <summary>
/// Classifies symbols into architecture tiers: Pure, Boundary, Runtime, Tooling.
/// Uses module metadata and symbol characteristics.
///
/// Detection priority:
/// 1. Module is marked Pure (IsPure flag from adapter) → Pure
/// 2. Module is marked Tooling (IsTooling flag) → Tooling
/// 3. Symbol is interface-only with no platform imports → Boundary
/// 4. Default → Runtime
/// </summary>
public static class TierClassifier
{
    public static TierAssignment[] Classify(SemanticGraph graph, ModuleInfo[]? modules)
    {
        // Build module lookup: file path → module info
        var fileToModule = new Dictionary<string, ModuleInfo>(StringComparer.OrdinalIgnoreCase);
        if (modules != null)
        {
            for (int m = 0; m < modules.Length; m++)
            {
                for (int f = 0; f < modules[m].FilePaths.Length; f++)
                    fileToModule[modules[m].FilePaths[f]] = modules[m];
            }
        }

        var results = new List<TierAssignment>();

        for (int i = 0; i < graph.Symbols.Length; i++)
        {
            var symbol = graph.Symbols[i];
            if (symbol.Kind != SymbolKind.File && symbol.Kind != SymbolKind.Module)
                continue;

            ArchitectureTier tier;
            string reason;

            if (symbol.Kind == SymbolKind.Module)
            {
                // Module-level: check flags
                var modInfo = modules?.FirstOrDefault(m => m.Name == symbol.Name);
                if (modInfo != null && modInfo.IsPure)
                {
                    tier = ArchitectureTier.Pure;
                    reason = "Module marked as pure (no platform references)";
                }
                else if (modInfo != null && modInfo.IsTooling)
                {
                    tier = ArchitectureTier.Tooling;
                    reason = "Module marked as tooling (editor/test only)";
                }
                else
                {
                    tier = ArchitectureTier.Runtime;
                    reason = "Default: module has platform references";
                }
            }
            else
            {
                // File-level: check owning module first, then heuristics
                if (fileToModule.TryGetValue(symbol.FilePath, out var owningModule))
                {
                    if (owningModule.IsPure)
                    {
                        tier = ArchitectureTier.Pure;
                        reason = $"File in pure module '{owningModule.Name}'";
                    }
                    else if (owningModule.IsTooling)
                    {
                        tier = ArchitectureTier.Tooling;
                        reason = $"File in tooling module '{owningModule.Name}'";
                    }
                    else if (IsInterfaceOnlyFile(graph, symbol))
                    {
                        tier = ArchitectureTier.Boundary;
                        reason = "Interface-only file in runtime module";
                    }
                    else
                    {
                        tier = ArchitectureTier.Runtime;
                        reason = $"File in runtime module '{owningModule.Name}'";
                    }
                }
                else
                {
                    // No module info: heuristic
                    tier = IsInterfaceOnlyFile(graph, symbol)
                        ? ArchitectureTier.Boundary
                        : ArchitectureTier.Runtime;
                    reason = tier == ArchitectureTier.Boundary
                        ? "Interface-only file (no module info)"
                        : "Default (no module info)";
                }
            }

            results.Add(new TierAssignment
            {
                SymbolId = symbol.Id,
                Tier = tier,
                Reason = reason,
            });
        }

        return results.ToArray();
    }

    private static bool IsInterfaceOnlyFile(SemanticGraph graph, Symbol fileSymbol)
    {
        bool hasTypes = false;
        bool allAbstract = true;

        foreach (var child in graph.ChildrenOf(fileSymbol.Id))
        {
            if (child.Kind == SymbolKind.Type)
            {
                hasTypes = true;
                if (!child.IsAbstract)
                {
                    allAbstract = false;
                    break;
                }
            }
        }

        return hasTypes && allAbstract;
    }
}
