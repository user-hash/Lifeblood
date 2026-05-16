#pragma once

#include "GraphModel.h"
#include "NativeEdgeClassification.h"
#include "NativeGraphFacts.h"
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
        unsigned includeEdgeCount = 0;
        unsigned callEdgeCount = 0;
        unsigned sameFileCallEdgeCount = 0;
        unsigned crossFileCallEdgeCount = 0;
        unsigned globalAccessEdgeCount = 0;
        unsigned fieldAccessEdgeCount = 0;
        unsigned parameterTypeEdgeCount = 0;
        unsigned callbackTargetEdgeCount = 0;
        unsigned functionDefinitionCount = 0;
        unsigned functionDeclarationCount = 0;
        unsigned macroCount = 0;
        unsigned globalVariableCount = 0;
        unsigned headerDeclaredSymbolCount = 0;
        unsigned translationUnitDeclaredSymbolCount = 0;
        unsigned callbackTableCount = 0;
        unsigned structCount = 0;
        unsigned unionCount = 0;
        unsigned enumCount = 0;
        unsigned typedefCount = 0;
        unsigned structFieldCount = 0;
        unsigned enumMemberCount = 0;
        NativeVisibilityCounts visibility;
    };

    static void AddNativeKindCounts(Counts& counts, const Symbol& symbol);
    static void AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol);
    void AddFileBucketDeclaredCount(
        Counts& counts,
        const std::string& symbolId,
        const Symbol& symbol) const;
    void AddEdgeCount(Counts& counts, const Edge& edge) const;
    static void WriteCounts(Symbol& module, const Counts& counts);

    NativeGraph& graph_;
    const NativeGraphOwnershipIndex& ownership_;
    std::map<std::string, Counts> counts_;
};
}
