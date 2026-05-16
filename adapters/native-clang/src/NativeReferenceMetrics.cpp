#include "NativeReferenceMetrics.h"

#include "NativeGraphMetricPropertyKeys.h"

namespace lifeblood::native_clang
{
NativeReferenceMetrics::NativeReferenceMetrics()
    : metrics_{{
        {
            MetricKind::Reference,
            NativeGraphMetricPropertyKeys::ReferenceInCount,
            NativeGraphMetricPropertyKeys::ReferenceOutCount,
        },
        {
            MetricKind::CallbackTarget,
            NativeGraphMetricPropertyKeys::CallbackTargetInCount,
            NativeGraphMetricPropertyKeys::CallbackTargetOutCount,
        },
        {
            MetricKind::GlobalAccess,
            NativeGraphMetricPropertyKeys::GlobalAccessInCount,
            NativeGraphMetricPropertyKeys::GlobalAccessOutCount,
        },
        {
            MetricKind::FieldAccess,
            NativeGraphMetricPropertyKeys::FieldAccessInCount,
            NativeGraphMetricPropertyKeys::FieldAccessOutCount,
        },
        {
            MetricKind::ParameterType,
            NativeGraphMetricPropertyKeys::ParameterTypeInCount,
            NativeGraphMetricPropertyKeys::ParameterTypeOutCount,
        },
        {
            MetricKind::EnumMember,
            NativeGraphMetricPropertyKeys::EnumMemberInCount,
            NativeGraphMetricPropertyKeys::EnumMemberOutCount,
        },
        {
            MetricKind::FieldType,
            NativeGraphMetricPropertyKeys::FieldTypeInCount,
            NativeGraphMetricPropertyKeys::FieldTypeOutCount,
        },
        {
            MetricKind::UnderlyingType,
            NativeGraphMetricPropertyKeys::UnderlyingTypeInCount,
            NativeGraphMetricPropertyKeys::UnderlyingTypeOutCount,
        },
        {
            MetricKind::GlobalType,
            NativeGraphMetricPropertyKeys::GlobalTypeInCount,
            NativeGraphMetricPropertyKeys::GlobalTypeOutCount,
        },
        {
            MetricKind::ReturnType,
            NativeGraphMetricPropertyKeys::ReturnTypeInCount,
            NativeGraphMetricPropertyKeys::ReturnTypeOutCount,
        },
        {
            MetricKind::TypeReference,
            NativeGraphMetricPropertyKeys::TypeReferenceInCount,
            NativeGraphMetricPropertyKeys::TypeReferenceOutCount,
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
