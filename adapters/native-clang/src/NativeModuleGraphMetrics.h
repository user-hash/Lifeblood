#pragma once

#include "GraphModel.h"
#include "NativeDeclaredSurfaceInventory.h"
#include "NativeEdgeClassification.h"
#include "NativeGraphFacts.h"
#include "NativeGraphOwnershipIndex.h"
#include "NativeKindInventory.h"
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
        NativeDeclaredSurfaceCounts declaredSurface;
        NativeKindInventoryCounts nativeKinds;
        NativeVisibilityCounts visibility;
    };

    static void AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol);
    void AddEdgeCount(Counts& counts, const Edge& edge) const;
    static void WriteCounts(Symbol& module, const Counts& counts);

    NativeGraph& graph_;
    const NativeGraphOwnershipIndex& ownership_;
    NativeDeclaredSurfaceInventory declaredSurface_;
    std::map<std::string, Counts> counts_;
};
}
