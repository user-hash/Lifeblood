#include "NativeGlobalFactsCollector.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"

namespace lifeblood::native_clang
{
NativeGlobalFactsCollector::NativeGlobalFactsCollector(const ClangSourceMapper& sourceMap)
    : sourceMap_(sourceMap)
{
}

std::optional<NativeGlobalDeclarationFacts> NativeGlobalFactsCollector::Collect(
    NativeCursorHandle handle) const
{
    if (!IsFileScopeCursor(handle)) return std::nullopt;

    auto cursor = handle.cursor;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return std::nullopt;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return std::nullopt;

    const auto storage = clang_Cursor_getStorageClass(cursor);
    NativeGlobalDeclarationFacts facts;
    facts.filePath = *file;
    facts.symbolId = GlobalVariableId(cursor);
    facts.name = name;
    facts.fieldType = NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
    facts.line = sourceMap_.Line(cursor);
    facts.isStatic = storage == CX_SC_Static;
    return facts;
}

void NativeGlobalFactsCollector::AddTypeReference(
    NativeCursorHandle handle,
    const std::string& globalId,
    NativeTypeEmitter& types) const
{
    auto cursor = handle.cursor;
    types.AddTypeReference(
        globalId,
        cursor,
        clang_getCursorType(cursor),
        NativeReferenceKinds::GlobalType);
}

bool NativeGlobalFactsCollector::IsFileScopeCursor(NativeCursorHandle handle) const
{
    auto parent = clang_getCursorSemanticParent(handle.cursor);
    return !clang_Cursor_isNull(parent) &&
           clang_getCursorKind(parent) == CXCursor_TranslationUnit;
}
}
