#include "NativeKindInventory.h"

#include "NativeCountPropertyWriter.h"
#include "NativeGraphFacts.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"

#include <array>

namespace lifeblood::native_clang
{
namespace
{
constexpr std::array<NativeCountProperty<NativeKindInventoryCounts>, 9> ModuleCountProperties{{
    { NativeGraphPropertyKeys::MacroCount, &NativeKindInventoryCounts::macroCount },
    { NativeGraphPropertyKeys::GlobalVariableCount, &NativeKindInventoryCounts::globalVariableCount },
    { NativeGraphPropertyKeys::CallbackTableCount, &NativeKindInventoryCounts::callbackTableCount },
    { NativeGraphPropertyKeys::StructCount, &NativeKindInventoryCounts::structCount },
    { NativeGraphPropertyKeys::UnionCount, &NativeKindInventoryCounts::unionCount },
    { NativeGraphPropertyKeys::EnumCount, &NativeKindInventoryCounts::enumCount },
    { NativeGraphPropertyKeys::TypedefCount, &NativeKindInventoryCounts::typedefCount },
    { NativeGraphPropertyKeys::StructFieldCount, &NativeKindInventoryCounts::structFieldCount },
    { NativeGraphPropertyKeys::EnumMemberCount, &NativeKindInventoryCounts::enumMemberCount },
}};

constexpr std::array<NativeCountProperty<NativeKindInventoryCounts>, 9> FileCountProperties{{
    { NativeGraphPropertyKeys::FileMacroCount, &NativeKindInventoryCounts::macroCount },
    { NativeGraphPropertyKeys::FileGlobalVariableCount, &NativeKindInventoryCounts::globalVariableCount },
    { NativeGraphPropertyKeys::FileCallbackTableCount, &NativeKindInventoryCounts::callbackTableCount },
    { NativeGraphPropertyKeys::FileStructCount, &NativeKindInventoryCounts::structCount },
    { NativeGraphPropertyKeys::FileUnionCount, &NativeKindInventoryCounts::unionCount },
    { NativeGraphPropertyKeys::FileEnumCount, &NativeKindInventoryCounts::enumCount },
    { NativeGraphPropertyKeys::FileTypedefCount, &NativeKindInventoryCounts::typedefCount },
    { NativeGraphPropertyKeys::FileStructFieldCount, &NativeKindInventoryCounts::structFieldCount },
    { NativeGraphPropertyKeys::FileEnumMemberCount, &NativeKindInventoryCounts::enumMemberCount },
}};
}

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
    WriteNativeCountProperties(module, counts, ModuleCountProperties);
}

void NativeKindInventory::WriteFileProperties(
    Symbol& file,
    const NativeKindInventoryCounts& counts)
{
    WriteNativeCountProperties(file, counts, FileCountProperties);
}
}
