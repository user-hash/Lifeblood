#pragma once

#include "GraphModel.h"

#include <iosfwd>

namespace lifeblood::native_clang
{
void WriteJsonGraph(std::ostream& output, const NativeGraph& graph);
}
