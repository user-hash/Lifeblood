#pragma once

#include <string>

namespace lifeblood::native_clang
{
struct NativeFunctionDeclarationFacts
{
    std::string filePath;
    std::string symbolId;
    std::string name;
    std::string signature;
    unsigned line = 0;
    bool isDefinition = false;
    bool isStatic = false;
};
}
