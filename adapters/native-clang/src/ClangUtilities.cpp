#include "ClangUtilities.h"

#include <algorithm>
#include <cctype>

namespace lifeblood::native_clang
{
std::string ToString(CXString value)
{
    const char* c = clang_getCString(value);
    std::string result = c == nullptr ? "" : c;
    clang_disposeString(value);
    return result;
}

std::string SlashPath(std::string value)
{
    std::replace(value.begin(), value.end(), '\\', '/');
    return value;
}

std::string Trim(std::string value)
{
    auto first = std::find_if_not(value.begin(), value.end(), [](unsigned char ch) {
        return std::isspace(ch) != 0;
    });
    auto last = std::find_if_not(value.rbegin(), value.rend(), [](unsigned char ch) {
        return std::isspace(ch) != 0;
    }).base();
    if (first >= last) return "";
    return std::string(first, last);
}

std::string NormalizeTypeForId(std::string value)
{
    value = Trim(value);
    const std::string structPrefix = "struct ";
    const std::string unionPrefix = "union ";
    const std::string enumPrefix = "enum ";
    if (value.rfind(structPrefix, 0) == 0) value.erase(0, structPrefix.size());
    if (value.rfind(unionPrefix, 0) == 0) value.erase(0, unionPrefix.size());
    if (value.rfind(enumPrefix, 0) == 0) value.erase(0, enumPrefix.size());

    std::string compact;
    compact.reserve(value.size());
    for (size_t i = 0; i < value.size(); i++)
    {
        if (std::isspace(static_cast<unsigned char>(value[i])) != 0)
        {
            bool nextIsPointer = i + 1 < value.size() && value[i + 1] == '*';
            bool prevIsPointer = !compact.empty() && compact.back() == '*';
            if (!nextIsPointer && !prevIsPointer)
                compact.push_back(' ');
        }
        else
        {
            compact.push_back(value[i]);
        }
    }
    return compact;
}

CXType StripPointers(CXType type)
{
    while (type.kind == CXType_Pointer)
        type = clang_getPointeeType(type);
    return type;
}

bool EndsWith(const std::string& value, const std::string& suffix)
{
    return value.size() >= suffix.size() &&
           std::equal(suffix.rbegin(), suffix.rend(), value.rbegin());
}
}
