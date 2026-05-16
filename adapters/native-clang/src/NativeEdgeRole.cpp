#include "NativeEdgeRole.h"

#include "NativeGraphPropertyKeys.h"

namespace lifeblood::native_clang
{
std::string NativeEdgeRole::For(const Edge& edge)
{
    auto referenceKind = edge.properties.find(NativeGraphPropertyKeys::ReferenceKind);
    if (referenceKind != edge.properties.end())
        return "reference:" + referenceKind->second;

    auto nativeKind = edge.properties.find(NativeGraphPropertyKeys::NativeKind);
    if (nativeKind != edge.properties.end())
        return "native:" + nativeKind->second;

    auto callKind = edge.properties.find(NativeGraphPropertyKeys::CallKind);
    if (callKind != edge.properties.end())
        return "call:" + callKind->second;

    return "";
}
}
