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
}

void NativeGraphBuilder::AddSymbol(Symbol symbol)
{
    graph_.symbols[symbol.id] = std::move(symbol);
}

void NativeGraphBuilder::AddEdge(Edge edge)
{
    auto key = std::make_tuple(edge.sourceId, edge.targetId, edge.kind);
    if (edgeKeys_.insert(key).second)
        graph_.edges.push_back(std::move(edge));
}

bool NativeGraphBuilder::HasSymbol(const std::string& symbolId) const
{
    return graph_.symbols.find(symbolId) != graph_.symbols.end();
}

Symbol* NativeGraphBuilder::FindSymbol(const std::string& symbolId)
{
    auto it = graph_.symbols.find(symbolId);
    return it == graph_.symbols.end() ? nullptr : &it->second;
}

const Symbol* NativeGraphBuilder::FindSymbol(const std::string& symbolId) const
{
    auto it = graph_.symbols.find(symbolId);
    return it == graph_.symbols.end() ? nullptr : &it->second;
}
}
