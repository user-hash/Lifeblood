#pragma once

#include "GraphModel.h"
#include "NativeEdgeMetricClassification.h"
#include "NativeGraphFacts.h"
#include "NativeGraphOwnershipIndex.h"
#include "NativeKindInventory.h"
#include "NativeVisibilityCounts.h"

#include <array>
#include <map>
#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class NativeFileGraphMetrics
{
public:
    NativeFileGraphMetrics(
        NativeGraph& graph,
        const NativeGraphOwnershipIndex& ownership);

    void ObserveSymbol(const std::string& symbolId, const Symbol& symbol);
    void ObserveEdge(const Edge& edge);
    void Write();

private:
    struct Counts
    {
        unsigned declaredSymbolCount = 0;
        NativeVisibilityCounts declaredVisibility;
        unsigned functionDefinitionCount = 0;
        unsigned functionDeclarationCount = 0;
        NativeKindInventoryCounts nativeKinds;
        unsigned outgoingReferenceEdgeCount = 0;
        unsigned incomingReferenceEdgeCount = 0;
        unsigned outgoingIncludeEdgeCount = 0;
        unsigned incomingIncludeEdgeCount = 0;
        unsigned outgoingGlobalAccessEdgeCount = 0;
        unsigned incomingGlobalAccessEdgeCount = 0;
        unsigned outgoingFieldAccessEdgeCount = 0;
        unsigned incomingFieldAccessEdgeCount = 0;
        unsigned outgoingParameterTypeEdgeCount = 0;
        unsigned incomingParameterTypeEdgeCount = 0;
        unsigned outgoingCallbackTargetEdgeCount = 0;
        unsigned incomingCallbackTargetEdgeCount = 0;
        unsigned outgoingCallEdgeCount = 0;
        unsigned incomingCallEdgeCount = 0;
        unsigned localCallEdgeCount = 0;
        unsigned outgoingCrossFileCallEdgeCount = 0;
        unsigned incomingCrossFileCallEdgeCount = 0;
    };

    using CountMember = unsigned Counts::*;

    struct CountProperty
    {
        const char* property;
        CountMember value;
    };

    void AddFileEdgeCount(const Edge& edge);
    void AddReferenceFileEdgeCounts(
        const NativeEdgeMetricClassification& metric,
        const std::optional<std::string>& sourceFileId,
        const std::optional<std::string>& targetFileId);
    void AddCallFileEdgeCounts(
        const Edge& edge,
        const std::optional<std::string>& sourceFileId,
        const std::optional<std::string>& targetFileId);
    void AddDirectionalFileCount(
        const std::optional<std::string>& sourceFileId,
        const std::optional<std::string>& targetFileId,
        CountMember outgoingCount,
        CountMember incomingCount);
    static void AddFunctionDeclarationCount(Counts& counts, const Symbol& symbol);
    static void WriteFileCounts(Symbol& file, const Counts& counts);
    void WriteSymbolCallCounts(Symbol& symbol) const;

    NativeGraph& graph_;
    const NativeGraphOwnershipIndex& ownership_;
    std::map<std::string, Counts> counts_;
    std::map<std::string, unsigned> sameFileCallOutCounts_;
    std::map<std::string, unsigned> sameFileCallInCounts_;
    std::map<std::string, unsigned> crossFileCallOutCounts_;
    std::map<std::string, unsigned> crossFileCallInCounts_;
};
}
