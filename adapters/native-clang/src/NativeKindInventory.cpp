#include "NativeKindInventory.h"

#include "NativeCountPropertyWriter.h"
#include "NativeGraphFacts.h"
#include "NativeGraphMetricPropertyKeys.h"
#include "NativeKindNames.h"

#include <array>

namespace lifeblood::native_clang
{
namespace
{
constexpr std::array<NativeCountProperty<NativeKindInventoryCounts>, 9> ModuleCountProperties{{
    { NativeGraphMetricPropertyKeys::MacroCount, &NativeKindInventoryCounts::macroCount },
    { NativeGraphMetricPropertyKeys::GlobalVariableCount, &NativeKindInventoryCounts::globalVariableCount },
    { NativeGraphMetricPropertyKeys::CallbackTableCount, &NativeKindInventoryCounts::callbackTableCount },
    { NativeGraphMetricPropertyKeys::StructCount, &NativeKindInventoryCounts::structCount },
    { NativeGraphMetricPropertyKeys::UnionCount, &NativeKindInventoryCounts::unionCount },
    { NativeGraphMetricPropertyKeys::EnumCount, &NativeKindInventoryCounts::enumCount },
    { NativeGraphMetricPropertyKeys::TypedefCount, &NativeKindInventoryCounts::typedefCount },
    { NativeGraphMetricPropertyKeys::StructFieldCount, &NativeKindInventoryCounts::structFieldCount },
    { NativeGraphMetricPropertyKeys::EnumMemberCount, &NativeKindInventoryCounts::enumMemberCount },
}};

constexpr std::array<NativeCountProperty<NativeKindInventoryCounts>, 9> FileCountProperties{{
    { NativeGraphMetricPropertyKeys::FileMacroCount, &NativeKindInventoryCounts::macroCount },
    { NativeGraphMetricPropertyKeys::FileGlobalVariableCount, &NativeKindInventoryCounts::globalVariableCount },
    { NativeGraphMetricPropertyKeys::FileCallbackTableCount, &NativeKindInventoryCounts::callbackTableCount },
    { NativeGraphMetricPropertyKeys::FileStructCount, &NativeKindInventoryCounts::structCount },
    { NativeGraphMetricPropertyKeys::FileUnionCount, &NativeKindInventoryCounts::unionCount },
    { NativeGraphMetricPropertyKeys::FileEnumCount, &NativeKindInventoryCounts::enumCount },
    { NativeGraphMetricPropertyKeys::FileTypedefCount, &NativeKindInventoryCounts::typedefCount },
    { NativeGraphMetricPropertyKeys::FileStructFieldCount, &NativeKindInventoryCounts::structFieldCount },
    { NativeGraphMetricPropertyKeys::FileEnumMemberCount, &NativeKindInventoryCounts::enumMemberCount },
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
