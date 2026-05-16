#pragma once

#include "GraphModel.h"
#include "NativeDirectionalSymbolCounts.h"
#include "NativeEdgeClassification.h"

#include <array>

namespace lifeblood::native_clang
{
class NativeReferenceMetrics
{
public:
    NativeReferenceMetrics();

    void Clear();
    void RecordAcceptedReference(const Edge& edge);
    void DecorateSymbol(Symbol& symbol) const;

private:
    enum class MetricKind
    {
        Reference,
        CallbackTarget,
        GlobalAccess,
        FieldAccess,
        ParameterType,
        EnumMember,
        FieldType,
        UnderlyingType,
        GlobalType,
        ReturnType,
        TypeReference
    };

    struct MetricCounter
    {
        MetricKind kind;
        const char* incomingProperty;
        const char* outgoingProperty;
        NativeDirectionalSymbolCounts counts;
    };

    static bool ShouldRecord(
        MetricKind metric,
        const NativeReferenceEdgeClassification& reference);

    std::array<MetricCounter, 11> metrics_;
};
}
