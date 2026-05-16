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
    referenceMetrics_.Clear();
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

    referenceMetrics_.DecorateSymbol(symbol);

    graph_.symbols[symbol.id] = std::move(symbol);
}

void NativeGraphBuilder::AddEdge(Edge edge)
{
    auto key = std::make_tuple(edge.sourceId, edge.targetId, edge.kind, EdgeRole(edge));
    if (edgeKeys_.insert(key).second)
    {
        if (edge.kind == "references")
        {
            referenceMetrics_.RecordAcceptedReference(edge);
            UpdateSymbol(edge.sourceId, [this](Symbol& symbol) {
                referenceMetrics_.DecorateSymbol(symbol);
            });
            UpdateSymbol(edge.targetId, [this](Symbol& symbol) {
                referenceMetrics_.DecorateSymbol(symbol);
            });
        }

        graph_.edges.push_back(std::move(edge));
    }
}

std::string NativeGraphBuilder::EdgeRole(const Edge& edge)
{
    auto referenceKind = edge.properties.find("native.referenceKind");
    if (referenceKind != edge.properties.end())
        return "reference:" + referenceKind->second;

    auto nativeKind = edge.properties.find("native.kind");
    if (nativeKind != edge.properties.end())
        return "native:" + nativeKind->second;

    auto callKind = edge.properties.find("native.callKind");
    if (callKind != edge.properties.end())
        return "call:" + callKind->second;

    return "";
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
}
