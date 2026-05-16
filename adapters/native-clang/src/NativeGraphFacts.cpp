#include "NativeGraphFacts.h"

namespace lifeblood::native_clang
{
bool NativeGraphFacts::HasNativeKind(const Symbol& symbol, const std::string& nativeKind)
{
    auto value = symbol.properties.find("native.kind");
    return value != symbol.properties.end() && value->second == nativeKind;
}

bool NativeGraphFacts::HasDeclarationKind(
    const Symbol& symbol,
    const std::string& declarationKind)
{
    auto value = symbol.properties.find("native.declarationKind");
    return value != symbol.properties.end() && value->second == declarationKind;
}

bool NativeGraphFacts::HasNativeEdgeKind(const Edge& edge, const std::string& nativeKind)
{
    auto value = edge.properties.find("native.kind");
    return value != edge.properties.end() && value->second == nativeKind;
}

bool NativeGraphFacts::HasReferenceKind(const Edge& edge, const std::string& referenceKind)
{
    auto value = edge.properties.find("native.referenceKind");
    return value != edge.properties.end() && value->second == referenceKind;
}
}
