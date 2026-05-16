#pragma once

#include "GraphModel.h"
#include "NativeFileGraphMetrics.h"
#include "NativeModuleGraphMetrics.h"

namespace lifeblood::native_clang
{
class NativeGraphFinalizer
{
public:
    void Finalize(NativeGraph& graph) const;
};
}
