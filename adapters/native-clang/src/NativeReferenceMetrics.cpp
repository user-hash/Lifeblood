#include "NativeReferenceMetrics.h"

#include "NativeGraphPropertyKeys.h"

namespace lifeblood::native_clang
{
NativeReferenceMetrics::NativeReferenceMetrics()
    : metrics_{{
        {
            MetricKind::Reference,
            NativeGraphPropertyKeys::ReferenceInCount,
            NativeGraphPropertyKeys::ReferenceOutCount,
        },
        {
            MetricKind::CallbackTarget,
            NativeGraphPropertyKeys::CallbackTargetInCount,
            NativeGraphPropertyKeys::CallbackTargetOutCount,
        },
        {
            MetricKind::GlobalAccess,
            NativeGraphPropertyKeys::GlobalAccessInCount,
            NativeGraphPropertyKeys::GlobalAccessOutCount,
        },
        {
            MetricKind::FieldAccess,
            NativeGraphPropertyKeys::FieldAccessInCount,
            NativeGraphPropertyKeys::FieldAccessOutCount,
        },
        {
            MetricKind::ParameterType,
            NativeGraphPropertyKeys::ParameterTypeInCount,
            NativeGraphPropertyKeys::ParameterTypeOutCount,
        },
        {
            MetricKind::EnumMember,
            NativeGraphPropertyKeys::EnumMemberInCount,
            NativeGraphPropertyKeys::EnumMemberOutCount,
        },
        {
            MetricKind::FieldType,
            NativeGraphPropertyKeys::FieldTypeInCount,
            NativeGraphPropertyKeys::FieldTypeOutCount,
        },
        {
            MetricKind::UnderlyingType,
            NativeGraphPropertyKeys::UnderlyingTypeInCount,
            NativeGraphPropertyKeys::UnderlyingTypeOutCount,
        },
        {
            MetricKind::GlobalType,
            NativeGraphPropertyKeys::GlobalTypeInCount,
            NativeGraphPropertyKeys::GlobalTypeOutCount,
        },
        {
            MetricKind::ReturnType,
            NativeGraphPropertyKeys::ReturnTypeInCount,
            NativeGraphPropertyKeys::ReturnTypeOutCount,
        },
        {
            MetricKind::TypeReference,
            NativeGraphPropertyKeys::TypeReferenceInCount,
            NativeGraphPropertyKeys::TypeReferenceOutCount,
        },
    }}
{
}

void NativeReferenceMetrics::Clear()
{
    for (auto& metric : metrics_)
        metric.counts.Clear();
}

void NativeReferenceMetrics::RecordAcceptedReference(const Edge& edge)
{
    auto reference = NativeEdgeClassification::Reference(edge);

    for (auto& metric : metrics_)
    {
        if (ShouldRecord(metric.kind, reference))
            metric.counts.Record(edge.sourceId, edge.targetId);
    }
}

void NativeReferenceMetrics::DecorateSymbol(Symbol& symbol) const
{
    for (const auto& metric : metrics_)
    {
        metric.counts.Decorate(
            symbol,
            metric.incomingProperty,
            metric.outgoingProperty);
    }
}

bool NativeReferenceMetrics::ShouldRecord(
    MetricKind metric,
    const NativeReferenceEdgeClassification& reference)
{
    switch (metric)
    {
    case MetricKind::Reference:
        return reference.isReference;
    case MetricKind::CallbackTarget:
        return reference.isCallbackTarget;
    case MetricKind::GlobalAccess:
        return reference.isGlobalAccess;
    case MetricKind::FieldAccess:
        return reference.isFieldAccess;
    case MetricKind::ParameterType:
        return reference.isParameterType;
    case MetricKind::EnumMember:
        return reference.isEnumMember;
    case MetricKind::FieldType:
        return reference.isFieldType;
    case MetricKind::UnderlyingType:
        return reference.isUnderlyingType;
    case MetricKind::GlobalType:
        return reference.isGlobalType;
    case MetricKind::ReturnType:
        return reference.isReturnType;
    case MetricKind::TypeReference:
        return reference.IsTypeReference();
    }

    return false;
}
}
