#pragma once

#include "GraphModel.h"

namespace lifeblood::native_clang
{
enum class NativeFunctionDeclarationRole
{
    None,
    Declaration,
    Definition
};

class NativeFunctionDeclarationClassifier
{
public:
    static NativeFunctionDeclarationRole Classify(const Symbol& symbol);
};
}
