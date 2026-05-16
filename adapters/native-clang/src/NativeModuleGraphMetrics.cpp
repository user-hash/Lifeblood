#include "NativeModuleGraphMetrics.h"

namespace lifeblood::native_clang
{
NativeModuleGraphMetrics::NativeModuleGraphMetrics(
    NativeGraph& graph,
    const NativeGraphOwnershipIndex& ownership)
    : graph_(graph),
      ownership_(ownership)
{
}

void NativeModuleGraphMetrics::ObserveSymbol(
    const std::string& symbolId,
    const Symbol& symbol)
{
    if (symbol.kind == "module")
    {
        counts_[symbolId];
        return;
    }

    auto moduleId = ownership_.OwningModuleId(symbolId);
    if (!moduleId) return;

    Counts& counts = counts_[*moduleId];
    counts.symbolCount++;
    NativeVisibilityCounter::Add(counts.visibility, symbol);
    AddFunctionDeclarationCount(counts, symbol);
    AddNativeKindCounts(counts, symbol);
}

void NativeModuleGraphMetrics::ObserveEdge(const Edge& edge)
{
    auto moduleId = ownership_.OwningModuleId(edge.sourceId);
    if (moduleId)
        AddEdgeCount(counts_[*moduleId], edge);
}

void NativeModuleGraphMetrics::Write()
{
    for (auto& [id, symbol] : graph_.symbols)
    {
        if (symbol.kind != "module") continue;

        auto counts = counts_.find(id);
        if (counts != counts_.end())
            WriteCounts(symbol, counts->second);
    }
}

void NativeModuleGraphMetrics::AddEdgeCount(Counts& counts, const Edge& edge)
{
    counts.edgeCount++;
    if (edge.kind == "references")
        counts.referenceEdgeCount++;
    else if (edge.kind == "calls")
        counts.callEdgeCount++;
}

void NativeModuleGraphMetrics::AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol)
{
    auto nativeKind = symbol.properties.find("native.kind");
    if (nativeKind == symbol.properties.end() || nativeKind->second != "function")
        return;

    auto declarationKind = symbol.properties.find("native.declarationKind");
    if (declarationKind != symbol.properties.end() && declarationKind->second == "declaration")
        counts.functionDeclarationCount++;
    else
        counts.functionDefinitionCount++;
}

bool NativeModuleGraphMetrics::HasNativeKind(
    const Symbol& symbol,
    const std::string& nativeKind)
{
    auto value = symbol.properties.find("native.kind");
    return value != symbol.properties.end() && value->second == nativeKind;
}

void NativeModuleGraphMetrics::AddNativeKindCounts(Counts& counts, const Symbol& symbol)
{
    if (HasNativeKind(symbol, "macro"))
        counts.macroCount++;
    if (HasNativeKind(symbol, "callbackTable"))
        counts.callbackTableCount++;
}

void NativeModuleGraphMetrics::WriteCounts(Symbol& module, const Counts& counts)
{
    module.properties["native.symbolCount"] = std::to_string(counts.symbolCount);
    module.properties["native.edgeCount"] = std::to_string(counts.edgeCount);
    module.properties["native.referenceEdgeCount"] = std::to_string(counts.referenceEdgeCount);
    module.properties["native.callEdgeCount"] = std::to_string(counts.callEdgeCount);
    module.properties["native.functionDefinitionCount"] =
        std::to_string(counts.functionDefinitionCount);
    module.properties["native.functionDeclarationCount"] =
        std::to_string(counts.functionDeclarationCount);
    module.properties["native.macroCount"] = std::to_string(counts.macroCount);
    module.properties["native.callbackTableCount"] =
        std::to_string(counts.callbackTableCount);
    NativeVisibilityCounter::Write(
        module,
        counts.visibility,
        "native.publicSymbolCount",
        "native.privateSymbolCount",
        "native.internalSymbolCount");
}
}
