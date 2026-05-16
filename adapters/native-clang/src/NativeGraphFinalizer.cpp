#include "NativeGraphFinalizer.h"

#include <set>

namespace lifeblood::native_clang
{
void NativeGraphFinalizer::Finalize(NativeGraph& graph) const
{
    std::map<std::string, ModuleCounts> moduleCounts;

    for (const auto& [id, symbol] : graph.symbols)
    {
        if (symbol.kind == "module")
        {
            moduleCounts[id];
            continue;
        }

        auto moduleId = OwningModuleId(graph, id);
        if (moduleId)
            moduleCounts[*moduleId].symbolCount++;
    }

    for (const auto& edge : graph.edges)
    {
        auto moduleId = OwningModuleId(graph, edge.sourceId);
        if (moduleId)
            AddEdgeCount(moduleCounts[*moduleId], edge);
    }

    for (auto& [id, symbol] : graph.symbols)
    {
        if (symbol.kind != "module") continue;

        auto counts = moduleCounts.find(id);
        if (counts != moduleCounts.end())
            WriteModuleCounts(symbol, counts->second);
    }
}

std::optional<std::string> NativeGraphFinalizer::OwningModuleId(
    const NativeGraph& graph,
    const std::string& symbolId)
{
    std::set<std::string> visited;
    std::string currentId = symbolId;

    while (!currentId.empty() && visited.insert(currentId).second)
    {
        auto current = graph.symbols.find(currentId);
        if (current == graph.symbols.end())
            return std::nullopt;

        if (current->second.kind == "module")
            return currentId;

        currentId = current->second.parentId;
    }

    return std::nullopt;
}

void NativeGraphFinalizer::AddEdgeCount(ModuleCounts& counts, const Edge& edge)
{
    counts.edgeCount++;
    if (edge.kind == "references")
        counts.referenceEdgeCount++;
    else if (edge.kind == "calls")
        counts.callEdgeCount++;
}

void NativeGraphFinalizer::WriteModuleCounts(Symbol& module, const ModuleCounts& counts)
{
    module.properties["native.symbolCount"] = std::to_string(counts.symbolCount);
    module.properties["native.edgeCount"] = std::to_string(counts.edgeCount);
    module.properties["native.referenceEdgeCount"] = std::to_string(counts.referenceEdgeCount);
    module.properties["native.callEdgeCount"] = std::to_string(counts.callEdgeCount);
}
}
