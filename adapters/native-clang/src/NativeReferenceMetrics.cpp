#include "NativeReferenceMetrics.h"

namespace lifeblood::native_clang
{
void NativeReferenceMetrics::Clear()
{
    referenceCounts_.Clear();
    callbackTargetCounts_.Clear();
    globalAccessCounts_.Clear();
    fieldAccessCounts_.Clear();
    parameterTypeCounts_.Clear();
    enumMemberCounts_.Clear();
    fieldTypeCounts_.Clear();
}

void NativeReferenceMetrics::RecordAcceptedReference(const Edge& edge)
{
    RecordReferenceCounts(edge.sourceId, edge.targetId);

    auto reference = NativeEdgeClassification::Reference(edge);
    if (reference.isCallbackTarget)
        RecordCallbackTargetCounts(edge.sourceId, edge.targetId);
    if (reference.isGlobalAccess)
        RecordGlobalAccessCounts(edge.sourceId, edge.targetId);
    if (reference.isFieldAccess)
        RecordFieldAccessCounts(edge.sourceId, edge.targetId);
    if (reference.isParameterType)
        RecordParameterTypeCounts(edge.sourceId, edge.targetId);
    if (reference.isEnumMember)
        RecordEnumMemberCounts(edge.sourceId, edge.targetId);
    if (reference.isFieldType)
        RecordFieldTypeCounts(edge.sourceId, edge.targetId);
}

void NativeReferenceMetrics::RecordReferenceCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    referenceCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordCallbackTargetCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    callbackTargetCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordGlobalAccessCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    globalAccessCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordFieldAccessCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    fieldAccessCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordParameterTypeCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    parameterTypeCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordEnumMemberCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    enumMemberCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::RecordFieldTypeCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    fieldTypeCounts_.Record(sourceId, targetId);
}

void NativeReferenceMetrics::DecorateSymbol(Symbol& symbol) const
{
    referenceCounts_.Decorate(
        symbol,
        "native.referenceInCount",
        "native.referenceOutCount");
    callbackTargetCounts_.Decorate(
        symbol,
        "native.callbackTargetInCount",
        "native.callbackTargetOutCount");
    globalAccessCounts_.Decorate(
        symbol,
        "native.globalAccessInCount",
        "native.globalAccessOutCount");
    fieldAccessCounts_.Decorate(
        symbol,
        "native.fieldAccessInCount",
        "native.fieldAccessOutCount");
    parameterTypeCounts_.Decorate(
        symbol,
        "native.parameterTypeInCount",
        "native.parameterTypeOutCount");
    enumMemberCounts_.Decorate(
        symbol,
        "native.enumMemberInCount",
        "native.enumMemberOutCount");
    fieldTypeCounts_.Decorate(
        symbol,
        "native.fieldTypeInCount",
        "native.fieldTypeOutCount");
}
}
