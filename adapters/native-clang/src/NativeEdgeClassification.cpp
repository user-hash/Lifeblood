#include "NativeEdgeClassification.h"

#include "NativeGraphFacts.h"

namespace lifeblood::native_clang
{
NativeReferenceEdgeClassification NativeEdgeClassification::Reference(const Edge& edge)
{
    NativeReferenceEdgeClassification classification;
    classification.isReference = edge.kind == "references";
    if (!classification.isReference)
        return classification;

    classification.isInclude = NativeGraphFacts::HasNativeEdgeKind(edge, "include");
    classification.isGlobalAccess = NativeGraphFacts::HasReferenceKind(edge, "globalAccess");
    classification.isFieldAccess = NativeGraphFacts::HasReferenceKind(edge, "fieldAccess");
    classification.isParameterType = NativeGraphFacts::HasReferenceKind(edge, "parameterType");
    classification.isCallbackTarget =
        NativeGraphFacts::HasReferenceKind(edge, "callbackTarget");
    classification.isEnumMember = NativeGraphFacts::HasReferenceKind(edge, "enumMember");
    classification.isFieldType = NativeGraphFacts::HasReferenceKind(edge, "fieldType");
    classification.isUnderlyingType =
        NativeGraphFacts::HasReferenceKind(edge, "underlyingType");
    return classification;
}

bool NativeEdgeClassification::IsCall(const Edge& edge)
{
    return edge.kind == "calls";
}
}
