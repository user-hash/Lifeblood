#include "NativeDeclaredSurfaceInventory.h"

#include "NativeGraphFacts.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"

namespace lifeblood::native_clang
{
NativeDeclaredSurfaceInventory::NativeDeclaredSurfaceInventory(
    const NativeGraph& graph,
    const NativeGraphOwnershipIndex& ownership)
    : graph_(graph),
      ownership_(ownership)
{
}

void NativeDeclaredSurfaceInventory::AddSymbol(
    NativeDeclaredSurfaceCounts& counts,
    const std::string& symbolId,
    const Symbol& symbol) const
{
    if (symbol.kind == "file") return;

    auto fileId = ownership_.OwningFileId(symbolId);
    if (!fileId) return;

    auto file = graph_.symbols.find(*fileId);
    if (file == graph_.symbols.end()) return;

    if (NativeGraphFacts::HasNativeKind(file->second, NativeKindNames::Header))
        counts.headerDeclaredSymbolCount++;
    else if (NativeGraphFacts::HasNativeKind(file->second, NativeKindNames::TranslationUnit))
        counts.translationUnitDeclaredSymbolCount++;
}

void NativeDeclaredSurfaceInventory::WriteModuleProperties(
    Symbol& module,
    const NativeDeclaredSurfaceCounts& counts)
{
    NativePropertyWriter::SetCount(
        module,
        NativeGraphPropertyKeys::HeaderDeclaredSymbolCount,
        counts.headerDeclaredSymbolCount);
    NativePropertyWriter::SetCount(
        module,
        NativeGraphPropertyKeys::TranslationUnitDeclaredSymbolCount,
        counts.translationUnitDeclaredSymbolCount);
}
}
