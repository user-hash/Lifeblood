#include "NativeGlobalEmitter.h"

#include "NativeFileRegistry.h"
#include "NativeGraphFacts.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativeLinkageNames.h"
#include "NativePropertyWriter.h"
#include "NativeVisibilityNames.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeGlobalEmitter::NativeGlobalEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    NativeFileRegistry& files)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      files_(files)
{
}

bool NativeGlobalEmitter::AddGlobalVariable(const NativeGlobalDeclarationFacts& facts)
{
    if (facts.filePath.empty() || facts.symbolId.empty() || facts.name.empty())
        return false;

    files_.EnsureFileSymbol(facts.filePath);

    const Symbol* existing = graph_.FindSymbol(facts.symbolId);
    const bool existingIsCallbackTable = existing != nullptr &&
        NativeGraphFacts::HasNativeKind(*existing, NativeKindNames::CallbackTable);
    const std::string nativeKind = existingIsCallbackTable
        ? NativeKindNames::CallbackTable
        : NativeKindNames::Global;

    Symbol symbol;
    symbol.id = facts.symbolId;
    symbol.name = facts.name;
    symbol.qualifiedName = facts.name;
    symbol.kind = "field";
    symbol.filePath = facts.filePath;
    symbol.line = facts.line;
    symbol.parentId = "file:" + facts.filePath;
    symbol.visibility = facts.isStatic
        ? NativeVisibilityNames::Private
        : NativeVisibilityNames::Public;
    symbol.isStatic = facts.isStatic;
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        nativeKind);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::Linkage,
        facts.isStatic ? NativeLinkageNames::Internal : NativeLinkageNames::External);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::FieldType,
        facts.fieldType);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    if (nativeKind == NativeKindNames::CallbackTable)
        NativePropertyWriter::SetTrue(symbol, NativeGraphPropertyKeys::CallbackTable);
    graph_.AddSymbol(symbol);

    return true;
}
}
