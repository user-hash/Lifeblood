#include "NativeEdgeMetricClassification.h"

#include "NativeEdgeClassification.h"

namespace lifeblood::native_clang
{
NativeEdgeMetricClassification NativeEdgeMetricClassifier::Classify(const Edge& edge)
{
    NativeEdgeMetricClassification metric;
    auto reference = NativeEdgeClassification::Reference(edge);
    metric.isReference = reference.isReference;
    metric.isInclude = reference.isInclude;
    metric.isGlobalAccess = reference.isGlobalAccess;
    metric.isFieldAccess = reference.isFieldAccess;
    metric.isParameterType = reference.isParameterType;
    metric.isCallbackTarget = reference.isCallbackTarget;
    metric.isCall = NativeEdgeClassification::IsCall(edge);
    return metric;
}
}
