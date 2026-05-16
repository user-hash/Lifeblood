#pragma once

#include "GraphModel.h"

#include <ostream>

namespace lifeblood::native_clang
{
void WriteJsonSymbol(std::ostream& output, const Symbol& symbol);
void WriteJsonEdge(std::ostream& output, const Edge& edge);
}
