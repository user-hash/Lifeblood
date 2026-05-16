#include "NativeGlobalEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeFileRegistry.h"
#include "NativeGraphFacts.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativeLinkageNames.h"
#include "NativePropertyWriter.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"
#include "NativeVisibilityNames.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeGlobalEmitter::NativeGlobalEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files,
    NativeTypeEmitter& types)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap),
      files_(files),
      types_(types)
{
}

bool NativeGlobalEmitter::AddGlobalVariable(CXCursor cursor)
{
    if (!IsFileScopeCursor(cursor)) return false;

    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;

    files_.EnsureFileSymbol(*file);

    const auto storage = clang_Cursor_getStorageClass(cursor);
    const std::string symbolId = GlobalVariableId(cursor);
    const Symbol* existing = graph_.FindSymbol(symbolId);
    const bool existingIsCallbackTable = existing != nullptr &&
        NativeGraphFacts::HasNativeKind(*existing, NativeKindNames::CallbackTable);
    const std::string nativeKind = existingIsCallbackTable
        ? NativeKindNames::CallbackTable
        : NativeKindNames::Global;

    Symbol symbol;
    symbol.id = symbolId;
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "field";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = storage == CX_SC_Static
        ? NativeVisibilityNames::Private
        : NativeVisibilityNames::Public;
    symbol.isStatic = storage == CX_SC_Static;
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        nativeKind);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::Linkage,
        storage == CX_SC_Static ? NativeLinkageNames::Internal : NativeLinkageNames::External);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::FieldType,
        NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(cursor)))));
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    if (nativeKind == NativeKindNames::CallbackTable)
        NativePropertyWriter::SetTrue(symbol, NativeGraphPropertyKeys::CallbackTable);
    graph_.AddSymbol(symbol);

    types_.AddTypeReference(
        symbol.id,
        cursor,
        clang_getCursorType(cursor),
        NativeReferenceKinds::GlobalType);
    return true;
}

bool NativeGlobalEmitter::IsFileScopeCursor(CXCursor cursor) const
{
    CXCursor parent = clang_getCursorSemanticParent(cursor);
    return !clang_Cursor_isNull(parent) &&
           clang_getCursorKind(parent) == CXCursor_TranslationUnit;
}
}
