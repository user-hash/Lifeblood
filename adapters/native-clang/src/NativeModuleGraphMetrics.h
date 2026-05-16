#pragma once

#include "GraphModel.h"
#include "NativeGraphOwnershipIndex.h"
#include "NativeVisibilityCounts.h"

#include <map>

namespace lifeblood::native_clang
{
class NativeModuleGraphMetrics
{
public:
    NativeModuleGraphMetrics(
        NativeGraph& graph,
        const NativeGraphOwnershipIndex& ownership);

    void ObserveSymbol(const std::string& symbolId, const Symbol& symbol);
    void ObserveEdge(const Edge& edge);
    void Write();

private:
    struct Counts
    {
        unsigned symbolCount = 0;
        unsigned edgeCount = 0;
        unsigned referenceEdgeCount = 0;
        unsigned callEdgeCount = 0;
        unsigned functionDefinitionCount = 0;
        unsigned functionDeclarationCount = 0;
        NativeVisibilityCounts visibility;
    };

    static void AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol);
    static void AddEdgeCount(Counts& counts, const Edge& edge);
    static void WriteCounts(Symbol& module, const Counts& counts);

    NativeGraph& graph_;
    const NativeGraphOwnershipIndex& ownership_;
    std::map<std::string, Counts> counts_;
};
}
