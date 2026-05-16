#include "NativeReferenceMetrics.h"

namespace lifeblood::native_clang
{
void NativeReferenceMetrics::Clear()
{
    referenceOutCounts_.clear();
    referenceInCounts_.clear();
    callbackTargetOutCounts_.clear();
    callbackTargetInCounts_.clear();
}

void NativeReferenceMetrics::RecordAcceptedReference(const Edge& edge)
{
    RecordReferenceCounts(edge.sourceId, edge.targetId);

    auto reference = NativeEdgeClassification::Reference(edge);
    if (reference.isCallbackTarget)
        RecordCallbackTargetCounts(edge.sourceId, edge.targetId);
}

void NativeReferenceMetrics::RecordReferenceCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    referenceOutCounts_[sourceId]++;
    referenceInCounts_[targetId]++;
}

void NativeReferenceMetrics::RecordCallbackTargetCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    callbackTargetOutCounts_[sourceId]++;
    callbackTargetInCounts_[targetId]++;
}

void NativeReferenceMetrics::DecorateSymbol(Symbol& symbol) const
{
    auto referenceOut = referenceOutCounts_.find(symbol.id);
    if (referenceOut != referenceOutCounts_.end())
        symbol.properties["native.referenceOutCount"] = std::to_string(referenceOut->second);

    auto referenceIn = referenceInCounts_.find(symbol.id);
    if (referenceIn != referenceInCounts_.end())
        symbol.properties["native.referenceInCount"] = std::to_string(referenceIn->second);

    auto callbackTargetOut = callbackTargetOutCounts_.find(symbol.id);
    if (callbackTargetOut != callbackTargetOutCounts_.end())
        symbol.properties["native.callbackTargetOutCount"] =
            std::to_string(callbackTargetOut->second);

    auto callbackTargetIn = callbackTargetInCounts_.find(symbol.id);
    if (callbackTargetIn != callbackTargetInCounts_.end())
        symbol.properties["native.callbackTargetInCount"] =
            std::to_string(callbackTargetIn->second);
}
}
