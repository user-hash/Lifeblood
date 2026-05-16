#pragma once

#include "GraphModel.h"

namespace lifeblood::native_clang
{
struct NativeKindInventoryCounts
{
    unsigned macroCount = 0;
    unsigned globalVariableCount = 0;
    unsigned callbackTableCount = 0;
    unsigned structCount = 0;
    unsigned unionCount = 0;
    unsigned enumCount = 0;
    unsigned typedefCount = 0;
    unsigned structFieldCount = 0;
    unsigned enumMemberCount = 0;
};

class NativeKindInventory
{
public:
    static void AddSymbol(NativeKindInventoryCounts& counts, const Symbol& symbol);
    static void WriteModuleProperties(Symbol& module, const NativeKindInventoryCounts& counts);
    static void WriteFileProperties(Symbol& file, const NativeKindInventoryCounts& counts);
};
}
