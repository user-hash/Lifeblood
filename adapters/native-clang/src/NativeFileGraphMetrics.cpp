#include "NativeFileGraphMetrics.h"

#include "NativeCountPropertyWriter.h"
#include "NativeFunctionDeclarationClassifier.h"
#include "NativeGraphMetricPropertyKeys.h"

namespace lifeblood::native_clang
{
NativeFileGraphMetrics::NativeFileGraphMetrics(
    NativeGraph& graph,
    const NativeGraphOwnershipIndex& ownership)
    : graph_(graph),
      ownership_(ownership)
{
}

void NativeFileGraphMetrics::ObserveSymbol(
    const std::string& symbolId,
    const Symbol& symbol)
{
    if (symbol.kind == "file")
    {
        counts_[symbolId];
        return;
    }

    if (symbol.filePath.empty()) return;

    Counts& counts = counts_["file:" + symbol.filePath];
    counts.declaredSymbolCount++;
    NativeVisibilityCounter::Add(counts.declaredVisibility, symbol);
    AddFunctionDeclarationCount(counts, symbol);
    NativeKindInventory::AddSymbol(counts.nativeKinds, symbol);
}

void NativeFileGraphMetrics::ObserveEdge(const Edge& edge)
{
    AddFileEdgeCount(edge);
}

void NativeFileGraphMetrics::Write()
{
    for (auto& [id, symbol] : graph_.symbols)
    {
        callSymbolMetrics_.Decorate(symbol);

        if (symbol.kind != "file") continue;

        auto counts = counts_.find(id);
        if (counts != counts_.end())
            WriteFileCounts(symbol, counts->second);
    }
}

void NativeFileGraphMetrics::AddFileEdgeCount(const Edge& edge)
{
    auto sourceFileId = ownership_.OwningFileId(edge.sourceId);
    auto targetFileId = ownership_.OwningFileId(edge.targetId);

    auto metric = NativeEdgeMetricClassifier::Classify(edge);
    if (metric.isReference)
        AddReferenceFileEdgeCounts(metric, sourceFileId, targetFileId);
    else if (metric.isCall)
        AddCallFileEdgeCounts(edge, sourceFileId, targetFileId);
}

void NativeFileGraphMetrics::AddReferenceFileEdgeCounts(
    const NativeEdgeMetricClassification& metric,
    const std::optional<std::string>& sourceFileId,
    const std::optional<std::string>& targetFileId)
{
    AddDirectionalFileCount(
        sourceFileId,
        targetFileId,
        &Counts::outgoingReferenceEdgeCount,
        &Counts::incomingReferenceEdgeCount);

    if (metric.isInclude)
        AddDirectionalFileCount(
            sourceFileId,
            targetFileId,
            &Counts::outgoingIncludeEdgeCount,
            &Counts::incomingIncludeEdgeCount);
    if (metric.isGlobalAccess)
        AddDirectionalFileCount(
            sourceFileId,
            targetFileId,
            &Counts::outgoingGlobalAccessEdgeCount,
            &Counts::incomingGlobalAccessEdgeCount);
    if (metric.isFieldAccess)
        AddDirectionalFileCount(
            sourceFileId,
            targetFileId,
            &Counts::outgoingFieldAccessEdgeCount,
            &Counts::incomingFieldAccessEdgeCount);
    if (metric.isParameterType)
        AddDirectionalFileCount(
            sourceFileId,
            targetFileId,
            &Counts::outgoingParameterTypeEdgeCount,
            &Counts::incomingParameterTypeEdgeCount);
    if (metric.isCallbackTarget)
        AddDirectionalFileCount(
            sourceFileId,
            targetFileId,
            &Counts::outgoingCallbackTargetEdgeCount,
            &Counts::incomingCallbackTargetEdgeCount);
}

void NativeFileGraphMetrics::AddCallFileEdgeCounts(
    const Edge& edge,
    const std::optional<std::string>& sourceFileId,
    const std::optional<std::string>& targetFileId)
{
    AddDirectionalFileCount(
        sourceFileId,
        targetFileId,
        &Counts::outgoingCallEdgeCount,
        &Counts::incomingCallEdgeCount);

    if (sourceFileId && targetFileId && *sourceFileId != *targetFileId)
    {
        counts_[*sourceFileId].outgoingCrossFileCallEdgeCount++;
        counts_[*targetFileId].incomingCrossFileCallEdgeCount++;
        callSymbolMetrics_.RecordCrossFileCall(edge.sourceId, edge.targetId);
    }
    else if (sourceFileId && targetFileId)
    {
        counts_[*sourceFileId].localCallEdgeCount++;
        callSymbolMetrics_.RecordSameFileCall(edge.sourceId, edge.targetId);
    }
}

void NativeFileGraphMetrics::AddDirectionalFileCount(
    const std::optional<std::string>& sourceFileId,
    const std::optional<std::string>& targetFileId,
    CountMember outgoingCount,
    CountMember incomingCount)
{
    if (sourceFileId)
        (counts_[*sourceFileId].*outgoingCount)++;
    if (targetFileId)
        (counts_[*targetFileId].*incomingCount)++;
}

void NativeFileGraphMetrics::AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol)
{
    auto role = NativeFunctionDeclarationClassifier::Classify(symbol);
    if (role == NativeFunctionDeclarationRole::Declaration)
        counts.functionDeclarationCount++;
    else if (role == NativeFunctionDeclarationRole::Definition)
        counts.functionDefinitionCount++;
}

void NativeFileGraphMetrics::WriteFileCounts(Symbol& file, const Counts& counts)
{
    constexpr std::array<NativeCountProperty<Counts>, 20> countProperties{{
        { NativeGraphMetricPropertyKeys::DeclaredSymbolCount, &Counts::declaredSymbolCount },
        { NativeGraphMetricPropertyKeys::FileFunctionDefinitionCount, &Counts::functionDefinitionCount },
        { NativeGraphMetricPropertyKeys::FileFunctionDeclarationCount, &Counts::functionDeclarationCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingReferenceEdgeCount, &Counts::outgoingReferenceEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingReferenceEdgeCount, &Counts::incomingReferenceEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingIncludeEdgeCount, &Counts::outgoingIncludeEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingIncludeEdgeCount, &Counts::incomingIncludeEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingGlobalAccessEdgeCount, &Counts::outgoingGlobalAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingGlobalAccessEdgeCount, &Counts::incomingGlobalAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingFieldAccessEdgeCount, &Counts::outgoingFieldAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingFieldAccessEdgeCount, &Counts::incomingFieldAccessEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingParameterTypeEdgeCount, &Counts::outgoingParameterTypeEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingParameterTypeEdgeCount, &Counts::incomingParameterTypeEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingCallbackTargetEdgeCount, &Counts::outgoingCallbackTargetEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingCallbackTargetEdgeCount, &Counts::incomingCallbackTargetEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingCallEdgeCount, &Counts::outgoingCallEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingCallEdgeCount, &Counts::incomingCallEdgeCount },
        { NativeGraphMetricPropertyKeys::FileLocalCallEdgeCount, &Counts::localCallEdgeCount },
        { NativeGraphMetricPropertyKeys::FileOutgoingCrossFileCallEdgeCount, &Counts::outgoingCrossFileCallEdgeCount },
        { NativeGraphMetricPropertyKeys::FileIncomingCrossFileCallEdgeCount, &Counts::incomingCrossFileCallEdgeCount },
    }};

    WriteNativeCountProperties(file, counts, countProperties);

    NativeVisibilityCounter::Write(
        file,
        counts.declaredVisibility,
        NativeGraphMetricPropertyKeys::PublicDeclaredSymbolCount,
        NativeGraphMetricPropertyKeys::PrivateDeclaredSymbolCount,
        NativeGraphMetricPropertyKeys::InternalDeclaredSymbolCount);
    NativeKindInventory::WriteFileProperties(file, counts.nativeKinds);
}
}
