#pragma once

#include "GraphModel.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeGraphFacts
{
public:
    static bool HasNativeKind(const Symbol& symbol, const std::string& nativeKind);
    static bool HasDeclarationKind(const Symbol& symbol, const std::string& declarationKind);
    static bool HasNativeEdgeKind(const Edge& edge, const std::string& nativeKind);
    static bool HasReferenceKind(const Edge& edge, const std::string& referenceKind);
};
}
