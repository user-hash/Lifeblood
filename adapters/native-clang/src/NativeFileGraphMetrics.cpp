#include "NativeFileGraphMetrics.h"

#include "NativeCountPropertyWriter.h"
#include "NativeFunctionDeclarationClassifier.h"
#include "NativeGraphPropertyKeys.h"
#include "NativePropertyWriter.h"

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
        WriteSymbolCallCounts(symbol);

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
        crossFileCallOutCounts_[edge.sourceId]++;
        crossFileCallInCounts_[edge.targetId]++;
    }
    else if (sourceFileId && targetFileId)
    {
        counts_[*sourceFileId].localCallEdgeCount++;
        sameFileCallOutCounts_[edge.sourceId]++;
        sameFileCallInCounts_[edge.targetId]++;
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
        { NativeGraphPropertyKeys::DeclaredSymbolCount, &Counts::declaredSymbolCount },
        { NativeGraphPropertyKeys::FileFunctionDefinitionCount, &Counts::functionDefinitionCount },
        { NativeGraphPropertyKeys::FileFunctionDeclarationCount, &Counts::functionDeclarationCount },
        { NativeGraphPropertyKeys::FileOutgoingReferenceEdgeCount, &Counts::outgoingReferenceEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingReferenceEdgeCount, &Counts::incomingReferenceEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingIncludeEdgeCount, &Counts::outgoingIncludeEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingIncludeEdgeCount, &Counts::incomingIncludeEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingGlobalAccessEdgeCount, &Counts::outgoingGlobalAccessEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingGlobalAccessEdgeCount, &Counts::incomingGlobalAccessEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingFieldAccessEdgeCount, &Counts::outgoingFieldAccessEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingFieldAccessEdgeCount, &Counts::incomingFieldAccessEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingParameterTypeEdgeCount, &Counts::outgoingParameterTypeEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingParameterTypeEdgeCount, &Counts::incomingParameterTypeEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingCallbackTargetEdgeCount, &Counts::outgoingCallbackTargetEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingCallbackTargetEdgeCount, &Counts::incomingCallbackTargetEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingCallEdgeCount, &Counts::outgoingCallEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingCallEdgeCount, &Counts::incomingCallEdgeCount },
        { NativeGraphPropertyKeys::FileLocalCallEdgeCount, &Counts::localCallEdgeCount },
        { NativeGraphPropertyKeys::FileOutgoingCrossFileCallEdgeCount, &Counts::outgoingCrossFileCallEdgeCount },
        { NativeGraphPropertyKeys::FileIncomingCrossFileCallEdgeCount, &Counts::incomingCrossFileCallEdgeCount },
    }};

    WriteNativeCountProperties(file, counts, countProperties);

    NativeVisibilityCounter::Write(
        file,
        counts.declaredVisibility,
        NativeGraphPropertyKeys::PublicDeclaredSymbolCount,
        NativeGraphPropertyKeys::PrivateDeclaredSymbolCount,
        NativeGraphPropertyKeys::InternalDeclaredSymbolCount);
    NativeKindInventory::WriteFileProperties(file, counts.nativeKinds);
}

void NativeFileGraphMetrics::WriteSymbolCallCounts(Symbol& symbol) const
{
    auto sameFileOutgoing = sameFileCallOutCounts_.find(symbol.id);
    if (sameFileOutgoing != sameFileCallOutCounts_.end())
        NativePropertyWriter::SetCount(
            symbol,
            NativeGraphPropertyKeys::SameFileDirectCallOutCount,
            sameFileOutgoing->second);

    auto sameFileIncoming = sameFileCallInCounts_.find(symbol.id);
    if (sameFileIncoming != sameFileCallInCounts_.end())
        NativePropertyWriter::SetCount(
            symbol,
            NativeGraphPropertyKeys::SameFileDirectCallInCount,
            sameFileIncoming->second);

    auto outgoing = crossFileCallOutCounts_.find(symbol.id);
    if (outgoing != crossFileCallOutCounts_.end())
        NativePropertyWriter::SetCount(
            symbol,
            NativeGraphPropertyKeys::CrossFileDirectCallOutCount,
            outgoing->second);

    auto incoming = crossFileCallInCounts_.find(symbol.id);
    if (incoming != crossFileCallInCounts_.end())
        NativePropertyWriter::SetCount(
            symbol,
            NativeGraphPropertyKeys::CrossFileDirectCallInCount,
            incoming->second);
}
}
