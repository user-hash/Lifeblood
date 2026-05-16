#include "NativeModuleGraphMetrics.h"

#include "NativeFunctionDeclarationClassifier.h"

namespace lifeblood::native_clang
{
NativeModuleGraphMetrics::NativeModuleGraphMetrics(
    NativeGraph& graph,
    const NativeGraphOwnershipIndex& ownership)
    : graph_(graph),
      ownership_(ownership),
      declaredSurface_(graph, ownership)
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
    NativeKindInventory::AddSymbol(counts.nativeKinds, symbol);
    declaredSurface_.AddSymbol(counts.declaredSurface, symbolId, symbol);
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

void NativeModuleGraphMetrics::AddEdgeCount(Counts& counts, const Edge& edge) const
{
    counts.edgeCount++;
    auto metric = NativeEdgeMetricClassifier::Classify(edge);
    if (metric.isReference)
    {
        counts.referenceEdgeCount++;
        if (metric.isInclude)
            counts.includeEdgeCount++;
        if (metric.isGlobalAccess)
            counts.globalAccessEdgeCount++;
        if (metric.isFieldAccess)
            counts.fieldAccessEdgeCount++;
        if (metric.isParameterType)
            counts.parameterTypeEdgeCount++;
        if (metric.isCallbackTarget)
            counts.callbackTargetEdgeCount++;
    }
    else if (metric.isCall)
    {
        counts.callEdgeCount++;
        auto sourceFile = ownership_.OwningFileId(edge.sourceId);
        auto targetFile = ownership_.OwningFileId(edge.targetId);
        if (sourceFile && targetFile && *sourceFile != *targetFile)
            counts.crossFileCallEdgeCount++;
        else if (sourceFile && targetFile)
            counts.sameFileCallEdgeCount++;
    }
}

void NativeModuleGraphMetrics::AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol)
{
    auto role = NativeFunctionDeclarationClassifier::Classify(symbol);
    if (role == NativeFunctionDeclarationRole::Declaration)
        counts.functionDeclarationCount++;
    else if (role == NativeFunctionDeclarationRole::Definition)
        counts.functionDefinitionCount++;
}

void NativeModuleGraphMetrics::WriteCounts(Symbol& module, const Counts& counts)
{
    module.properties["native.symbolCount"] = std::to_string(counts.symbolCount);
    module.properties["native.edgeCount"] = std::to_string(counts.edgeCount);
    module.properties["native.referenceEdgeCount"] = std::to_string(counts.referenceEdgeCount);
    module.properties["native.includeEdgeCount"] = std::to_string(counts.includeEdgeCount);
    module.properties["native.callEdgeCount"] = std::to_string(counts.callEdgeCount);
    module.properties["native.sameFileCallEdgeCount"] =
        std::to_string(counts.sameFileCallEdgeCount);
    module.properties["native.crossFileCallEdgeCount"] =
        std::to_string(counts.crossFileCallEdgeCount);
    module.properties["native.globalAccessEdgeCount"] =
        std::to_string(counts.globalAccessEdgeCount);
    module.properties["native.fieldAccessEdgeCount"] =
        std::to_string(counts.fieldAccessEdgeCount);
    module.properties["native.parameterTypeEdgeCount"] =
        std::to_string(counts.parameterTypeEdgeCount);
    module.properties["native.callbackTargetEdgeCount"] =
        std::to_string(counts.callbackTargetEdgeCount);
    module.properties["native.functionDefinitionCount"] =
        std::to_string(counts.functionDefinitionCount);
    module.properties["native.functionDeclarationCount"] =
        std::to_string(counts.functionDeclarationCount);
    NativeDeclaredSurfaceInventory::WriteModuleProperties(module, counts.declaredSurface);
    NativeKindInventory::WriteModuleProperties(module, counts.nativeKinds);
    NativeVisibilityCounter::Write(
        module,
        counts.visibility,
        "native.publicSymbolCount",
        "native.privateSymbolCount",
        "native.internalSymbolCount");
}
}
