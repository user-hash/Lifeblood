#pragma once

#include <string>

namespace lifeblood::native_clang
{
struct NativeEnumConstantDeclarationFacts
{
    std::string filePath;
    std::string symbolId;
    std::string name;
    std::string qualifiedName;
    std::string parentId;
    std::string enumValue;
    unsigned line = 0;
};

struct NativeFieldDeclarationFacts
{
    std::string filePath;
    std::string symbolId;
    std::string name;
    std::string qualifiedName;
    std::string parentId;
    std::string fieldType;
    unsigned line = 0;
};
}
