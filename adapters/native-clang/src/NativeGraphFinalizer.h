#pragma once

#include "GraphModel.h"

#include <map>
#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class NativeGraphFinalizer
{
public:
    void Finalize(NativeGraph& graph) const;

private:
    struct ModuleCounts
    {
        unsigned symbolCount = 0;
        unsigned edgeCount = 0;
        unsigned referenceEdgeCount = 0;
        unsigned callEdgeCount = 0;
    };

    static std::optional<std::string> OwningModuleId(
        const NativeGraph& graph,
        const std::string& symbolId);
    static void AddEdgeCount(ModuleCounts& counts, const Edge& edge);
    static void WriteModuleCounts(Symbol& module, const ModuleCounts& counts);
};
}
