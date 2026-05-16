#pragma once

#include "GraphModel.h"

namespace lifeblood::native_clang
{
struct NativeEdgeMetricClassification
{
    bool isReference = false;
    bool isInclude = false;
    bool isGlobalAccess = false;
    bool isFieldAccess = false;
    bool isParameterType = false;
    bool isCallbackTarget = false;
    bool isCall = false;
};

class NativeEdgeMetricClassifier
{
public:
    static NativeEdgeMetricClassification Classify(const Edge& edge);
};
}
