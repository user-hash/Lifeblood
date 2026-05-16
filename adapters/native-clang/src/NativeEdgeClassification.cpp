#include "NativeEdgeClassification.h"

#include "NativeGraphFacts.h"
#include "NativeReferenceKinds.h"

namespace lifeblood::native_clang
{
NativeReferenceEdgeClassification NativeEdgeClassification::Reference(const Edge& edge)
{
    NativeReferenceEdgeClassification classification;
    classification.isReference = edge.kind == "references";
    if (!classification.isReference)
        return classification;

    classification.isInclude = NativeGraphFacts::HasNativeEdgeKind(edge, "include");
    classification.isGlobalAccess = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::GlobalAccess);
    classification.isFieldAccess = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::FieldAccess);
    classification.isParameterType = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::ParameterType);
    classification.isCallbackTarget =
        NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::CallbackTarget);
    classification.isEnumMember = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::EnumMember);
    classification.isFieldType = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::FieldType);
    classification.isUnderlyingType =
        NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::UnderlyingType);
    classification.isGlobalType = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::GlobalType);
    classification.isReturnType = NativeGraphFacts::HasReferenceKind(edge, NativeReferenceKinds::ReturnType);
    return classification;
}

bool NativeReferenceEdgeClassification::IsTypeReference() const
{
    return isParameterType ||
           isFieldType ||
           isUnderlyingType ||
           isGlobalType ||
           isReturnType;
}

bool NativeEdgeClassification::IsCall(const Edge& edge)
{
    return edge.kind == "calls";
}
}
