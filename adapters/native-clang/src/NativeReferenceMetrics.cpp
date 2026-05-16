#include "NativeReferenceMetrics.h"

namespace lifeblood::native_clang
{
NativeReferenceMetrics::NativeReferenceMetrics()
    : metrics_{{
        {
            MetricKind::Reference,
            "native.referenceInCount",
            "native.referenceOutCount",
        },
        {
            MetricKind::CallbackTarget,
            "native.callbackTargetInCount",
            "native.callbackTargetOutCount",
        },
        {
            MetricKind::GlobalAccess,
            "native.globalAccessInCount",
            "native.globalAccessOutCount",
        },
        {
            MetricKind::FieldAccess,
            "native.fieldAccessInCount",
            "native.fieldAccessOutCount",
        },
        {
            MetricKind::ParameterType,
            "native.parameterTypeInCount",
            "native.parameterTypeOutCount",
        },
        {
            MetricKind::EnumMember,
            "native.enumMemberInCount",
            "native.enumMemberOutCount",
        },
        {
            MetricKind::FieldType,
            "native.fieldTypeInCount",
            "native.fieldTypeOutCount",
        },
        {
            MetricKind::UnderlyingType,
            "native.underlyingTypeInCount",
            "native.underlyingTypeOutCount",
        },
        {
            MetricKind::GlobalType,
            "native.globalTypeInCount",
            "native.globalTypeOutCount",
        },
        {
            MetricKind::ReturnType,
            "native.returnTypeInCount",
            "native.returnTypeOutCount",
        },
        {
            MetricKind::TypeReference,
            "native.typeReferenceInCount",
            "native.typeReferenceOutCount",
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
