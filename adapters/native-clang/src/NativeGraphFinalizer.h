#pragma once

#include "GraphModel.h"
#include "NativeGraphOwnershipIndex.h"

#include <map>
#include <string>

namespace lifeblood::native_clang
{
class NativeGraphFinalizer
{
public:
    void Finalize(NativeGraph& graph) const;

private:
    struct ModuleCounts
    {
        unsigned symbolCount = 0;
        unsigned edgeCount = 0;
        unsigned referenceEdgeCount = 0;
        unsigned callEdgeCount = 0;
        unsigned publicSymbolCount = 0;
        unsigned privateSymbolCount = 0;
        unsigned internalSymbolCount = 0;
    };

    struct FileCounts
    {
        unsigned declaredSymbolCount = 0;
        unsigned publicDeclaredSymbolCount = 0;
        unsigned privateDeclaredSymbolCount = 0;
        unsigned internalDeclaredSymbolCount = 0;
        unsigned outgoingReferenceEdgeCount = 0;
        unsigned incomingReferenceEdgeCount = 0;
        unsigned outgoingCallEdgeCount = 0;
        unsigned incomingCallEdgeCount = 0;
        unsigned outgoingCrossFileCallEdgeCount = 0;
        unsigned incomingCrossFileCallEdgeCount = 0;
    };

    static void AddEdgeCount(ModuleCounts& counts, const Edge& edge);
    static void AddVisibilityCount(
        unsigned& publicCount,
        unsigned& privateCount,
        unsigned& internalCount,
        const Symbol& symbol);
    static void WriteModuleCounts(Symbol& module, const ModuleCounts& counts);
    static void AddFileEdgeCount(
        std::map<std::string, FileCounts>& fileCounts,
        const NativeGraphOwnershipIndex& ownership,
        const Edge& edge,
        std::map<std::string, unsigned>& crossFileCallOutCounts,
        std::map<std::string, unsigned>& crossFileCallInCounts);
    static void WriteFileCounts(Symbol& file, const FileCounts& counts);
    static void WriteCrossFileCallCounts(
        Symbol& symbol,
        const std::map<std::string, unsigned>& crossFileCallOutCounts,
        const std::map<std::string, unsigned>& crossFileCallInCounts);
};
}
