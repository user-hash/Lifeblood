#pragma once

#include "GraphModel.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeEdgeRole
{
public:
    static std::string For(const Edge& edge);
};
}
