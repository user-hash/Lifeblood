#include "NativeModuleGraphMetrics.h"

#include "NativeCountPropertyWriter.h"
#include "NativeFunctionDeclarationClassifier.h"
#include "NativeGraphMetricPropertyKeys.h"

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
    constexpr std::array<NativeCountProperty<Counts>, 13> countProperties{{
        { NativeGraphMetricPropertyKeys::SymbolCount, &Counts::symbolCount },
        { NativeGraphMetricPropertyKeys::EdgeCount, &Counts::edgeCount },
        { NativeGraphMetricPropertyKeys::ReferenceEdgeCount, &Counts::referenceEdgeCount },
        { NativeGraphMetricPropertyKeys::IncludeEdgeCount, &Counts::includeEdgeCount },
        { NativeGraphMetricPropertyKeys::CallEdgeCount, &Counts::callEdgeCount },
        { NativeGraphMetricPropertyKeys::SameFileCallEdgeCount, &Counts::sameFileCallEdgeCount },
        { NativeGraphMetricPropertyKeys::CrossFileCallEdgeCount, &Counts::crossFileCallEdgeCount },
        { NativeGraphMetricPropertyKeys::GlobalAccessEdgeCount, &Counts::globalAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::FieldAccessEdgeCount, &Counts::fieldAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::ParameterTypeEdgeCount, &Counts::parameterTypeEdgeCount },
        { NativeGraphMetricPropertyKeys::CallbackTargetEdgeCount, &Counts::callbackTargetEdgeCount },
        { NativeGraphMetricPropertyKeys::FunctionDefinitionCount, &Counts::functionDefinitionCount },
        { NativeGraphMetricPropertyKeys::FunctionDeclarationCount, &Counts::functionDeclarationCount },
    }};

    WriteNativeCountProperties(module, counts, countProperties);

    NativeDeclaredSurfaceInventory::WriteModuleProperties(module, counts.declaredSurface);
    NativeKindInventory::WriteModuleProperties(module, counts.nativeKinds);
    NativeVisibilityCounter::Write(
        module,
        counts.visibility,
        NativeGraphMetricPropertyKeys::PublicSymbolCount,
        NativeGraphMetricPropertyKeys::PrivateSymbolCount,
        NativeGraphMetricPropertyKeys::InternalSymbolCount);
}
}
