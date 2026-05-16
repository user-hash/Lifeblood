#pragma once

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
std::string FunctionId(CXCursor cursor);
std::string TypeId(CXCursor cursor);
std::string GlobalVariableId(CXCursor cursor);
std::string MacroId(const std::string& name);
std::string EnumConstantId(CXCursor cursor, const std::string& enumTypeId);
std::string OwnerNameFromTypeId(const std::string& typeId);
}
