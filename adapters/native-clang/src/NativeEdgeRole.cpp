#include "NativeEdgeRole.h"

namespace lifeblood::native_clang
{
std::string NativeEdgeRole::For(const Edge& edge)
{
    auto referenceKind = edge.properties.find("native.referenceKind");
    if (referenceKind != edge.properties.end())
        return "reference:" + referenceKind->second;

    auto nativeKind = edge.properties.find("native.kind");
    if (nativeKind != edge.properties.end())
        return "native:" + nativeKind->second;

    auto callKind = edge.properties.find("native.callKind");
    if (callKind != edge.properties.end())
        return "call:" + callKind->second;

    return "";
}
}
