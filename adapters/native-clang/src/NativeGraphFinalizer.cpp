#include "NativeGraphFinalizer.h"

#include <set>

namespace lifeblood::native_clang
{
void NativeGraphFinalizer::Finalize(NativeGraph& graph) const
{
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

        auto moduleId = OwningModuleId(graph, id);
        if (moduleId)
            moduleCounts[*moduleId].symbolCount++;

        if (symbol.kind == "file")
        {
            fileCounts[id];
            continue;
        }

        if (!symbol.filePath.empty())
            fileCounts["file:" + symbol.filePath].declaredSymbolCount++;
    }

    for (const auto& edge : graph.edges)
    {
        auto moduleId = OwningModuleId(graph, edge.sourceId);
        if (moduleId)
            AddEdgeCount(moduleCounts[*moduleId], edge);

        AddFileEdgeCount(
            fileCounts,
            graph,
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

std::optional<std::string> NativeGraphFinalizer::OwningModuleId(
    const NativeGraph& graph,
    const std::string& symbolId)
{
    std::set<std::string> visited;
    std::string currentId = symbolId;

    while (!currentId.empty() && visited.insert(currentId).second)
    {
        auto current = graph.symbols.find(currentId);
        if (current == graph.symbols.end())
            return std::nullopt;

        if (current->second.kind == "module")
            return currentId;

        currentId = current->second.parentId;
    }

    return std::nullopt;
}

std::optional<std::string> NativeGraphFinalizer::OwningFileId(
    const NativeGraph& graph,
    const std::string& symbolId)
{
    auto symbol = graph.symbols.find(symbolId);
    if (symbol == graph.symbols.end())
        return std::nullopt;

    if (symbol->second.kind == "file")
        return symbolId;

    if (!symbol->second.filePath.empty())
        return "file:" + symbol->second.filePath;

    return std::nullopt;
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
}

void NativeGraphFinalizer::AddFileEdgeCount(
    std::map<std::string, FileCounts>& fileCounts,
    const NativeGraph& graph,
    const Edge& edge,
    std::map<std::string, unsigned>& crossFileCallOutCounts,
    std::map<std::string, unsigned>& crossFileCallInCounts)
{
    auto sourceFileId = OwningFileId(graph, edge.sourceId);
    auto targetFileId = OwningFileId(graph, edge.targetId);

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
