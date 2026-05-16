#include "NativeSymbolIds.h"

#include "ClangUtilities.h"

#include <sstream>
#include <vector>

namespace lifeblood::native_clang
{
std::string FunctionId(CXCursor cursor)
{
    std::string name = ToString(clang_getCursorSpelling(cursor));
    std::vector<std::string> parameters;
    const int count = clang_Cursor_getNumArguments(cursor);
    for (int i = 0; i < count; i++)
    {
        CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
        parameters.push_back(NormalizeTypeForId(
            ToString(clang_getTypeSpelling(clang_getCursorType(arg)))));
    }

    std::ostringstream id;
    id << "method:" << name << "(";
    for (size_t i = 0; i < parameters.size(); i++)
    {
        if (i > 0) id << ",";
        id << parameters[i];
    }
    id << ")";
    return id.str();
}

std::string TypeId(CXCursor cursor)
{
    return "type:" + ToString(clang_getCursorSpelling(cursor));
}

std::string GlobalVariableId(CXCursor cursor)
{
    return "field:" + ToString(clang_getCursorSpelling(cursor));
}

std::string MacroId(const std::string& name)
{
    return "field:macro:" + name;
}

std::string EnumConstantId(CXCursor cursor, const std::string& enumTypeId)
{
    return "field:" + OwnerNameFromTypeId(enumTypeId) + "." + ToString(clang_getCursorSpelling(cursor));
}

std::string OwnerNameFromTypeId(const std::string& typeId)
{
    const std::string prefix = "type:";
    return typeId.rfind(prefix, 0) == 0
        ? typeId.substr(prefix.size())
        : typeId;
}
}
