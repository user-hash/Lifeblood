#include "NativeKindInventory.h"

#include "NativeGraphFacts.h"
#include "NativeKindNames.h"

namespace lifeblood::native_clang
{
void NativeKindInventory::AddSymbol(
    NativeKindInventoryCounts& counts,
    const Symbol& symbol)
{
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Macro))
        counts.macroCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Global))
        counts.globalVariableCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::CallbackTable))
        counts.callbackTableCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Struct))
        counts.structCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Union))
        counts.unionCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Enum))
        counts.enumCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::Typedef))
        counts.typedefCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::StructField))
        counts.structFieldCount++;
    if (NativeGraphFacts::HasNativeKind(symbol, NativeKindNames::EnumMember))
        counts.enumMemberCount++;
}

void NativeKindInventory::WriteModuleProperties(
    Symbol& module,
    const NativeKindInventoryCounts& counts)
{
    module.properties["native.macroCount"] = std::to_string(counts.macroCount);
    module.properties["native.globalVariableCount"] =
        std::to_string(counts.globalVariableCount);
    module.properties["native.callbackTableCount"] =
        std::to_string(counts.callbackTableCount);
    module.properties["native.structCount"] = std::to_string(counts.structCount);
    module.properties["native.unionCount"] = std::to_string(counts.unionCount);
    module.properties["native.enumCount"] = std::to_string(counts.enumCount);
    module.properties["native.typedefCount"] = std::to_string(counts.typedefCount);
    module.properties["native.structFieldCount"] =
        std::to_string(counts.structFieldCount);
    module.properties["native.enumMemberCount"] =
        std::to_string(counts.enumMemberCount);
}

void NativeKindInventory::WriteFileProperties(
    Symbol& file,
    const NativeKindInventoryCounts& counts)
{
    file.properties["native.fileMacroCount"] = std::to_string(counts.macroCount);
    file.properties["native.fileGlobalVariableCount"] =
        std::to_string(counts.globalVariableCount);
    file.properties["native.fileCallbackTableCount"] =
        std::to_string(counts.callbackTableCount);
    file.properties["native.fileStructCount"] = std::to_string(counts.structCount);
    file.properties["native.fileUnionCount"] = std::to_string(counts.unionCount);
    file.properties["native.fileEnumCount"] = std::to_string(counts.enumCount);
    file.properties["native.fileTypedefCount"] = std::to_string(counts.typedefCount);
    file.properties["native.fileStructFieldCount"] =
        std::to_string(counts.structFieldCount);
    file.properties["native.fileEnumMemberCount"] =
        std::to_string(counts.enumMemberCount);
}
}
