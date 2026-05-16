#include "NativeModuleGraphMetrics.h"

#include "NativeFunctionDeclarationClassifier.h"
#include "NativePropertyWriter.h"

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
        AddReferenceEdgeCount(counts, metric);
    else if (metric.isCall)
        AddCallEdgeCount(counts, edge);
}

void NativeModuleGraphMetrics::AddReferenceEdgeCount(
    Counts& counts,
    const NativeEdgeMetricClassification& metric) const
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

void NativeModuleGraphMetrics::AddCallEdgeCount(Counts& counts, const Edge& edge) const
{
    counts.callEdgeCount++;
    auto sourceFile = ownership_.OwningFileId(edge.sourceId);
    auto targetFile = ownership_.OwningFileId(edge.targetId);
    if (sourceFile && targetFile && *sourceFile != *targetFile)
        counts.crossFileCallEdgeCount++;
    else if (sourceFile && targetFile)
        counts.sameFileCallEdgeCount++;
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
    constexpr std::array<CountProperty, 13> countProperties{{
        { "native.symbolCount", &Counts::symbolCount },
        { "native.edgeCount", &Counts::edgeCount },
        { "native.referenceEdgeCount", &Counts::referenceEdgeCount },
        { "native.includeEdgeCount", &Counts::includeEdgeCount },
        { "native.callEdgeCount", &Counts::callEdgeCount },
        { "native.sameFileCallEdgeCount", &Counts::sameFileCallEdgeCount },
        { "native.crossFileCallEdgeCount", &Counts::crossFileCallEdgeCount },
        { "native.globalAccessEdgeCount", &Counts::globalAccessEdgeCount },
        { "native.fieldAccessEdgeCount", &Counts::fieldAccessEdgeCount },
        { "native.parameterTypeEdgeCount", &Counts::parameterTypeEdgeCount },
        { "native.callbackTargetEdgeCount", &Counts::callbackTargetEdgeCount },
        { "native.functionDefinitionCount", &Counts::functionDefinitionCount },
        { "native.functionDeclarationCount", &Counts::functionDeclarationCount },
    }};

    for (const auto& countProperty : countProperties)
        NativePropertyWriter::SetCount(
            module,
            countProperty.property,
            counts.*countProperty.value);

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
