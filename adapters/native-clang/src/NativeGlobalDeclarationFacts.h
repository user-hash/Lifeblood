#pragma once

#include <string>

namespace lifeblood::native_clang
{
struct NativeGlobalDeclarationFacts
{
    std::string filePath;
    std::string symbolId;
    std::string name;
    std::string fieldType;
    unsigned line = 0;
    bool isStatic = false;
};
}
