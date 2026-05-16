#include "NativeFileGraphMetrics.h"

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
    AddNativeKindCounts(counts, symbol);
}

void NativeFileGraphMetrics::ObserveEdge(const Edge& edge)
{
    AddFileEdgeCount(edge);
}

void NativeFileGraphMetrics::Write()
{
    for (auto& [id, symbol] : graph_.symbols)
    {
        WriteCrossFileCallCounts(symbol);

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

    if (edge.kind == "references")
    {
        if (sourceFileId)
            counts_[*sourceFileId].outgoingReferenceEdgeCount++;
        if (targetFileId)
            counts_[*targetFileId].incomingReferenceEdgeCount++;

        if (NativeGraphFacts::HasNativeEdgeKind(edge, "include"))
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingIncludeEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingIncludeEdgeCount++;
        }

        if (NativeGraphFacts::HasReferenceKind(edge, "callbackTarget"))
        {
            if (sourceFileId)
                counts_[*sourceFileId].outgoingCallbackTargetEdgeCount++;
            if (targetFileId)
                counts_[*targetFileId].incomingCallbackTargetEdgeCount++;
        }
    }
    else if (edge.kind == "calls")
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
    }
}

void NativeFileGraphMetrics::AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol)
{
    if (!NativeGraphFacts::HasNativeKind(symbol, "function"))
        return;

    if (NativeGraphFacts::HasDeclarationKind(symbol, "declaration"))
        counts.functionDeclarationCount++;
    else
        counts.functionDefinitionCount++;
}

void NativeFileGraphMetrics::AddNativeKindCounts(Counts& counts, const Symbol& symbol)
{
    if (NativeGraphFacts::HasNativeKind(symbol, "macro"))
        counts.macroCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "global"))
        counts.globalVariableCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "callbackTable"))
        counts.callbackTableCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "struct"))
        counts.structCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "union"))
        counts.unionCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "enum"))
        counts.enumCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "typedef"))
        counts.typedefCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "structField"))
        counts.structFieldCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, "enumMember"))
        counts.enumMemberCount++;
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
    file.properties["native.fileMacroCount"] = std::to_string(counts.macroCount);
    file.properties["native.fileGlobalVariableCount"] =
        std::to_string(counts.globalVariableCount);
    file.properties["native.fileCallbackTableCount"] =
        std::to_string(counts.callbackTableCount);
    file.properties["native.fileStructCount"] = std::to_string(counts.structCount);
    file.properties["native.fileUnionCount"] = std::to_string(counts.unionCount);
    file.properties["native.fileEnumCount"] = std::to_string(counts.enumCount);
    file.properties["native.fileTypedefCount"] = std::to_string(counts.typedefCount);
    file.properties["native.fileStructFieldCount"] =
        std::to_string(counts.structFieldCount);
    file.properties["native.fileEnumMemberCount"] =
        std::to_string(counts.enumMemberCount);
    file.properties["native.fileOutgoingReferenceEdgeCount"] =
        std::to_string(counts.outgoingReferenceEdgeCount);
    file.properties["native.fileIncomingReferenceEdgeCount"] =
        std::to_string(counts.incomingReferenceEdgeCount);
    file.properties["native.fileOutgoingIncludeEdgeCount"] =
        std::to_string(counts.outgoingIncludeEdgeCount);
    file.properties["native.fileIncomingIncludeEdgeCount"] =
        std::to_string(counts.incomingIncludeEdgeCount);
    file.properties["native.fileOutgoingCallbackTargetEdgeCount"] =
        std::to_string(counts.outgoingCallbackTargetEdgeCount);
    file.properties["native.fileIncomingCallbackTargetEdgeCount"] =
        std::to_string(counts.incomingCallbackTargetEdgeCount);
    file.properties["native.fileOutgoingCallEdgeCount"] =
        std::to_string(counts.outgoingCallEdgeCount);
    file.properties["native.fileIncomingCallEdgeCount"] =
        std::to_string(counts.incomingCallEdgeCount);
    file.properties["native.fileOutgoingCrossFileCallEdgeCount"] =
        std::to_string(counts.outgoingCrossFileCallEdgeCount);
    file.properties["native.fileIncomingCrossFileCallEdgeCount"] =
        std::to_string(counts.incomingCrossFileCallEdgeCount);
}

void NativeFileGraphMetrics::WriteCrossFileCallCounts(Symbol& symbol) const
{
    auto outgoing = crossFileCallOutCounts_.find(symbol.id);
    if (outgoing != crossFileCallOutCounts_.end())
        symbol.properties["native.crossFileDirectCallOutCount"] = std::to_string(outgoing->second);

    auto incoming = crossFileCallInCounts_.find(symbol.id);
    if (incoming != crossFileCallInCounts_.end())
        symbol.properties["native.crossFileDirectCallInCount"] = std::to_string(incoming->second);
}
}
