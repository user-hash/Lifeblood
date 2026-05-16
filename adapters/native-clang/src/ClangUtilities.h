#pragma once

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
std::string ToString(CXString value);
std::string SlashPath(std::string value);
std::string Trim(std::string value);
std::string NormalizeTypeForId(std::string value);
CXType StripPointers(CXType type);
bool EndsWith(const std::string& value, const std::string& suffix);
}
