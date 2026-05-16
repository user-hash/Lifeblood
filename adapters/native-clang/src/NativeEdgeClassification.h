#pragma once

#include "GraphModel.h"

namespace lifeblood::native_clang
{
struct NativeReferenceEdgeClassification
{
    bool isReference = false;
    bool isInclude = false;
    bool isGlobalAccess = false;
    bool isFieldAccess = false;
    bool isParameterType = false;
    bool isCallbackTarget = false;
    bool isEnumMember = false;
    bool isFieldType = false;
    bool isUnderlyingType = false;
    bool isGlobalType = false;
    bool isReturnType = false;

    bool IsTypeReference() const;
};

class NativeEdgeClassification
{
public:
    static NativeReferenceEdgeClassification Reference(const Edge& edge);
    static bool IsCall(const Edge& edge);
};
}
