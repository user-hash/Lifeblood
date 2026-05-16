#include "NativeFileCallSymbolMetrics.h"

#include "NativeGraphMetricPropertyKeys.h"

namespace lifeblood::native_clang
{
void NativeFileCallSymbolMetrics::RecordSameFileCall(
    const std::string& sourceId,
    const std::string& targetId)
{
    sameFileCalls_.Record(sourceId, targetId);
}

void NativeFileCallSymbolMetrics::RecordCrossFileCall(
    const std::string& sourceId,
    const std::string& targetId)
{
    crossFileCalls_.Record(sourceId, targetId);
}

void NativeFileCallSymbolMetrics::Decorate(Symbol& symbol) const
{
    sameFileCalls_.Decorate(
        symbol,
        NativeGraphMetricPropertyKeys::SameFileDirectCallInCount,
        NativeGraphMetricPropertyKeys::SameFileDirectCallOutCount);
    crossFileCalls_.Decorate(
        symbol,
        NativeGraphMetricPropertyKeys::CrossFileDirectCallInCount,
        NativeGraphMetricPropertyKeys::CrossFileDirectCallOutCount);
}
}
