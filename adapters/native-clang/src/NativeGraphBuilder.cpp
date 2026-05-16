#include "NativeGraphBuilder.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeGraphBuilder::NativeGraphBuilder(NativeGraph& graph)
    : graph_(graph)
{
}

void NativeGraphBuilder::Clear()
{
    graph_.symbols.clear();
    graph_.edges.clear();
    edgeKeys_.clear();
    referenceOutCounts_.clear();
    referenceInCounts_.clear();
}

void NativeGraphBuilder::AddSymbol(Symbol symbol)
{
    auto existing = graph_.symbols.find(symbol.id);
    if (existing != graph_.symbols.end())
    {
        for (const auto& [key, value] : existing->second.properties)
        {
            if (symbol.properties.find(key) == symbol.properties.end())
                symbol.properties[key] = value;
        }
    }

    auto referenceOut = referenceOutCounts_.find(symbol.id);
    if (referenceOut != referenceOutCounts_.end() &&
        symbol.properties.find("native.referenceOutCount") == symbol.properties.end())
    {
        symbol.properties["native.referenceOutCount"] = std::to_string(referenceOut->second);
    }

    auto referenceIn = referenceInCounts_.find(symbol.id);
    if (referenceIn != referenceInCounts_.end() &&
        symbol.properties.find("native.referenceInCount") == symbol.properties.end())
    {
        symbol.properties["native.referenceInCount"] = std::to_string(referenceIn->second);
    }

    graph_.symbols[symbol.id] = std::move(symbol);
}

void NativeGraphBuilder::AddEdge(Edge edge)
{
    auto key = std::make_tuple(edge.sourceId, edge.targetId, edge.kind);
    if (edgeKeys_.insert(key).second)
    {
        if (edge.kind == "references")
            RecordReferenceCounts(edge.sourceId, edge.targetId);

        graph_.edges.push_back(std::move(edge));
    }
}

bool NativeGraphBuilder::HasSymbol(const std::string& symbolId) const
{
    return graph_.symbols.find(symbolId) != graph_.symbols.end();
}

const Symbol* NativeGraphBuilder::FindSymbol(const std::string& symbolId) const
{
    auto it = graph_.symbols.find(symbolId);
    return it == graph_.symbols.end() ? nullptr : &it->second;
}

void NativeGraphBuilder::UpdateSymbol(
    const std::string& symbolId,
    const std::function<void(Symbol&)>& update)
{
    auto it = graph_.symbols.find(symbolId);
    if (it == graph_.symbols.end()) return;

    update(it->second);
}

void NativeGraphBuilder::RecordReferenceCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    const auto outCount = ++referenceOutCounts_[sourceId];
    UpdateSymbol(sourceId, [&](Symbol& symbol) {
        symbol.properties["native.referenceOutCount"] = std::to_string(outCount);
    });

    const auto inCount = ++referenceInCounts_[targetId];
    UpdateSymbol(targetId, [&](Symbol& symbol) {
        symbol.properties["native.referenceInCount"] = std::to_string(inCount);
    });
}
}
