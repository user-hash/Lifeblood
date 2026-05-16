#pragma once

#include "GraphModel.h"

#include <string>

namespace lifeblood::native_clang
{
struct NativeVisibilityCounts
{
    unsigned publicCount = 0;
    unsigned privateCount = 0;
    unsigned internalCount = 0;
};

class NativeVisibilityCounter
{
public:
    static void Add(NativeVisibilityCounts& counts, const Symbol& symbol);
    static void Write(
        Symbol& symbol,
        const NativeVisibilityCounts& counts,
        const std::string& publicProperty,
        const std::string& privateProperty,
        const std::string& internalProperty);
};
}
