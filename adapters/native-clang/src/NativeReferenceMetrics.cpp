#include "NativeReferenceMetrics.h"

namespace lifeblood::native_clang
{
void NativeReferenceMetrics::Clear()
{
    referenceOutCounts_.clear();
    referenceInCounts_.clear();
}

void NativeReferenceMetrics::RecordAcceptedReference(
    const std::string& sourceId,
    const std::string& targetId)
{
    referenceOutCounts_[sourceId]++;
    referenceInCounts_[targetId]++;
}

void NativeReferenceMetrics::DecorateSymbol(Symbol& symbol) const
{
    auto referenceOut = referenceOutCounts_.find(symbol.id);
    if (referenceOut != referenceOutCounts_.end())
        symbol.properties["native.referenceOutCount"] = std::to_string(referenceOut->second);

    auto referenceIn = referenceInCounts_.find(symbol.id);
    if (referenceIn != referenceInCounts_.end())
        symbol.properties["native.referenceInCount"] = std::to_string(referenceIn->second);
}
}
