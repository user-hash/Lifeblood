#pragma once

#include "GraphModel.h"

#include <string>

namespace lifeblood::native_clang
{
class NativePropertyWriter
{
public:
    static void Set(Symbol& symbol, const std::string& property, const std::string& value);
    static void Set(Edge& edge, const std::string& property, const std::string& value);
    static void SetTrue(Symbol& symbol, const std::string& property);
    static void SetCount(Symbol& symbol, const std::string& property, unsigned value);
};
}
