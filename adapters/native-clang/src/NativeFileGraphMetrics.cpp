#include "NativeFileGraphMetrics.h"

#include "NativeKindNames.h"

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

    auto reference = NativeEdgeClassification::Reference(edge);
    if (reference.isReference)
    {
        if (sourceFileId)
            counts_[*sourceFileId].outgoingReferenceEdgeCount++;
        if (targetFileId)
            counts_[*targetFileId].incomingReferenceEdgeCount++;

        if (reference.isInclude)
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingIncludeEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingIncludeEdgeCount++;
        }

        if (reference.isGlobalAccess)
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingGlobalAccessEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingGlobalAccessEdgeCount++;
        }

        if (reference.isFieldAccess)
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingFieldAccessEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingFieldAccessEdgeCount++;
        }

        if (reference.isParameterType)
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingParameterTypeEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingParameterTypeEdgeCount++;
        }

        if (reference.isCallbackTarget)
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingCallbackTargetEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingCallbackTargetEdgeCount++;
        }
    }
    else if (NativeEdgeClassification::IsCall(edge))
    {
        if (sourceFileId)
            counts_[*sourceFileId].outgoingCallEdgeCount++;
        if (targetFileId)
            counts_[*targetFileId].incomingCallEdgeCount++;

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
}

void NativeFileGraphMetrics::AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol)
{
    if (!NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Function))
        return;

    if (NativeGraphFacts::HasDeclarationKind(symbol, "declaration"))
        counts.functionDeclarationCount++;
    else
        counts.functionDefinitionCount++;
}

void NativeFileGraphMetrics::WriteFileCounts(Symbol& file, const Counts& counts)
{
    file.properties["native.declaredSymbolCount"] =
        std::to_string(counts.declaredSymbolCount);
    NativeVisibilityCounter::Write(
        file,
        counts.declaredVisibility,
        "native.publicDeclaredSymbolCount",
        "native.privateDeclaredSymbolCount",
        "native.internalDeclaredSymbolCount");
    file.properties["native.fileFunctionDefinitionCount"] =
        std::to_string(counts.functionDefinitionCount);
    file.properties["native.fileFunctionDeclarationCount"] =
        std::to_string(counts.functionDeclarationCount);
    NativeKindInventory::WriteFileProperties(file, counts.nativeKinds);
    file.properties["native.fileOutgoingReferenceEdgeCount"] =
        std::to_string(counts.outgoingReferenceEdgeCount);
    file.properties["native.fileIncomingReferenceEdgeCount"] =
        std::to_string(counts.incomingReferenceEdgeCount);
    file.properties["native.fileOutgoingIncludeEdgeCount"] =
        std::to_string(counts.outgoingIncludeEdgeCount);
    file.properties["native.fileIncomingIncludeEdgeCount"] =
        std::to_string(counts.incomingIncludeEdgeCount);
    file.properties["native.fileOutgoingGlobalAccessEdgeCount"] =
        std::to_string(counts.outgoingGlobalAccessEdgeCount);
    file.properties["native.fileIncomingGlobalAccessEdgeCount"] =
        std::to_string(counts.incomingGlobalAccessEdgeCount);
    file.properties["native.fileOutgoingFieldAccessEdgeCount"] =
        std::to_string(counts.outgoingFieldAccessEdgeCount);
    file.properties["native.fileIncomingFieldAccessEdgeCount"] =
        std::to_string(counts.incomingFieldAccessEdgeCount);
    file.properties["native.fileOutgoingParameterTypeEdgeCount"] =
        std::to_string(counts.outgoingParameterTypeEdgeCount);
    file.properties["native.fileIncomingParameterTypeEdgeCount"] =
        std::to_string(counts.incomingParameterTypeEdgeCount);
    file.properties["native.fileOutgoingCallbackTargetEdgeCount"] =
        std::to_string(counts.outgoingCallbackTargetEdgeCount);
    file.properties["native.fileIncomingCallbackTargetEdgeCount"] =
        std::to_string(counts.incomingCallbackTargetEdgeCount);
    file.properties["native.fileOutgoingCallEdgeCount"] =
        std::to_string(counts.outgoingCallEdgeCount);
    file.properties["native.fileIncomingCallEdgeCount"] =
        std::to_string(counts.incomingCallEdgeCount);
    file.properties["native.fileLocalCallEdgeCount"] =
        std::to_string(counts.localCallEdgeCount);
    file.properties["native.fileOutgoingCrossFileCallEdgeCount"] =
        std::to_string(counts.outgoingCrossFileCallEdgeCount);
    file.properties["native.fileIncomingCrossFileCallEdgeCount"] =
        std::to_string(counts.incomingCrossFileCallEdgeCount);
}

void NativeFileGraphMetrics::WriteSymbolCallCounts(Symbol& symbol) const
{
    auto sameFileOutgoing = sameFileCallOutCounts_.find(symbol.id);
    if (sameFileOutgoing != sameFileCallOutCounts_.end())
        symbol.properties["native.sameFileDirectCallOutCount"] =
            std::to_string(sameFileOutgoing->second);

    auto sameFileIncoming = sameFileCallInCounts_.find(symbol.id);
    if (sameFileIncoming != sameFileCallInCounts_.end())
        symbol.properties["native.sameFileDirectCallInCount"] =
            std::to_string(sameFileIncoming->second);

    auto outgoing = crossFileCallOutCounts_.find(symbol.id);
    if (outgoing != crossFileCallOutCounts_.end())
        symbol.properties["native.crossFileDirectCallOutCount"] = std::to_string(outgoing->second);

    auto incoming = crossFileCallInCounts_.find(symbol.id);
    if (incoming != crossFileCallInCounts_.end())
        symbol.properties["native.crossFileDirectCallInCount"] = std::to_string(incoming->second);
}
}
