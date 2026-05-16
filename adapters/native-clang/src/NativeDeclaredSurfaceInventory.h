#pragma once

#include "GraphModel.h"
#include "NativeGraphOwnershipIndex.h"

#include <string>

namespace lifeblood::native_clang
{
struct NativeDeclaredSurfaceCounts
{
    unsigned headerDeclaredSymbolCount = 0;
    unsigned translationUnitDeclaredSymbolCount = 0;
};

class NativeDeclaredSurfaceInventory
{
public:
    NativeDeclaredSurfaceInventory(
        const NativeGraph& graph,
        const NativeGraphOwnershipIndex& ownership);

    void AddSymbol(
        NativeDeclaredSurfaceCounts& counts,
        const std::string& symbolId,
        const Symbol& symbol) const;

    static void WriteModuleProperties(
        Symbol& module,
        const NativeDeclaredSurfaceCounts& counts);

private:
    const NativeGraph& graph_;
    const NativeGraphOwnershipIndex& ownership_;
};
}
