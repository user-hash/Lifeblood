#include "NativeGraphFinalizer.h"

namespace lifeblood::native_clang
{
void NativeGraphFinalizer::Finalize(NativeGraph& graph) const
{
    NativeGraphOwnershipIndex ownership(graph);
    std::map<std::string, ModuleCounts> moduleCounts;
    std::map<std::string, FileCounts> fileCounts;
    std::map<std::string, unsigned> crossFileCallOutCounts;
    std::map<std::string, unsigned> crossFileCallInCounts;

    for (const auto& [id, symbol] : graph.symbols)
    {
        if (symbol.kind == "module")
        {
            moduleCounts[id];
            continue;
        }

        auto moduleId = ownership.OwningModuleId(id);
        if (moduleId)
        {
            moduleCounts[*moduleId].symbolCount++;
            NativeVisibilityCounter::Add(moduleCounts[*moduleId].visibility, symbol);
        }

        if (symbol.kind == "file")
        {
            fileCounts[id];
            continue;
        }

        if (!symbol.filePath.empty())
        {
            fileCounts["file:" + symbol.filePath].declaredSymbolCount++;
            NativeVisibilityCounter::Add(
                fileCounts["file:" + symbol.filePath].declaredVisibility,
                symbol);
        }
    }

    for (const auto& edge : graph.edges)
    {
        auto moduleId = ownership.OwningModuleId(edge.sourceId);
        if (moduleId)
            AddEdgeCount(moduleCounts[*moduleId], edge);

        AddFileEdgeCount(
            fileCounts,
            ownership,
            edge,
            crossFileCallOutCounts,
            crossFileCallInCounts);
    }

    for (auto& [id, symbol] : graph.symbols)
    {
        WriteCrossFileCallCounts(symbol, crossFileCallOutCounts, crossFileCallInCounts);

        if (symbol.kind == "module")
        {
            auto counts = moduleCounts.find(id);
            if (counts != moduleCounts.end())
                WriteModuleCounts(symbol, counts->second);
            continue;
        }

        if (symbol.kind == "file")
        {
            auto counts = fileCounts.find(id);
            if (counts != fileCounts.end())
                WriteFileCounts(symbol, counts->second);
        }
    }
}

void NativeGraphFinalizer::AddEdgeCount(ModuleCounts& counts, const Edge& edge)
{
    counts.edgeCount++;
    if (edge.kind == "references")
        counts.referenceEdgeCount++;
    else if (edge.kind == "calls")
        counts.callEdgeCount++;
}

void NativeGraphFinalizer::WriteModuleCounts(Symbol& module, const ModuleCounts& counts)
{
    module.properties["native.symbolCount"] = std::to_string(counts.symbolCount);
    module.properties["native.edgeCount"] = std::to_string(counts.edgeCount);
    module.properties["native.referenceEdgeCount"] = std::to_string(counts.referenceEdgeCount);
    module.properties["native.callEdgeCount"] = std::to_string(counts.callEdgeCount);
    NativeVisibilityCounter::Write(
        module,
        counts.visibility,
        "native.publicSymbolCount",
        "native.privateSymbolCount",
        "native.internalSymbolCount");
}

void NativeGraphFinalizer::AddFileEdgeCount(
    std::map<std::string, FileCounts>& fileCounts,
    const NativeGraphOwnershipIndex& ownership,
    const Edge& edge,
    std::map<std::string, unsigned>& crossFileCallOutCounts,
    std::map<std::string, unsigned>& crossFileCallInCounts)
{
    auto sourceFileId = ownership.OwningFileId(edge.sourceId);
    auto targetFileId = ownership.OwningFileId(edge.targetId);

    if (edge.kind == "references")
    {
        if (sourceFileId)
            fileCounts[*sourceFileId].outgoingReferenceEdgeCount++;
        if (targetFileId)
            fileCounts[*targetFileId].incomingReferenceEdgeCount++;
    }
    else if (edge.kind == "calls")
    {
        if (sourceFileId)
            fileCounts[*sourceFileId].outgoingCallEdgeCount++;
        if (targetFileId)
            fileCounts[*targetFileId].incomingCallEdgeCount++;

        if (sourceFileId && targetFileId && *sourceFileId != *targetFileId)
        {
            fileCounts[*sourceFileId].outgoingCrossFileCallEdgeCount++;
            fileCounts[*targetFileId].incomingCrossFileCallEdgeCount++;
            crossFileCallOutCounts[edge.sourceId]++;
            crossFileCallInCounts[edge.targetId]++;
        }
    }
}

void NativeGraphFinalizer::WriteFileCounts(Symbol& file, const FileCounts& counts)
{
    file.properties["native.declaredSymbolCount"] =
        std::to_string(counts.declaredSymbolCount);
    NativeVisibilityCounter::Write(
        file,
        counts.declaredVisibility,
        "native.publicDeclaredSymbolCount",
        "native.privateDeclaredSymbolCount",
        "native.internalDeclaredSymbolCount");
    file.properties["native.fileOutgoingReferenceEdgeCount"] =
        std::to_string(counts.outgoingReferenceEdgeCount);
    file.properties["native.fileIncomingReferenceEdgeCount"] =
        std::to_string(counts.incomingReferenceEdgeCount);
    file.properties["native.fileOutgoingCallEdgeCount"] =
        std::to_string(counts.outgoingCallEdgeCount);
    file.properties["native.fileIncomingCallEdgeCount"] =
        std::to_string(counts.incomingCallEdgeCount);
    file.properties["native.fileOutgoingCrossFileCallEdgeCount"] =
        std::to_string(counts.outgoingCrossFileCallEdgeCount);
    file.properties["native.fileIncomingCrossFileCallEdgeCount"] =
        std::to_string(counts.incomingCrossFileCallEdgeCount);
}

void NativeGraphFinalizer::WriteCrossFileCallCounts(
    Symbol& symbol,
    const std::map<std::string, unsigned>& crossFileCallOutCounts,
    const std::map<std::string, unsigned>& crossFileCallInCounts)
{
    auto outgoing = crossFileCallOutCounts.find(symbol.id);
    if (outgoing != crossFileCallOutCounts.end())
        symbol.properties["native.crossFileDirectCallOutCount"] = std::to_string(outgoing->second);

    auto incoming = crossFileCallInCounts.find(symbol.id);
    if (incoming != crossFileCallInCounts.end())
        symbol.properties["native.crossFileDirectCallInCount"] = std::to_string(incoming->second);
}
}
