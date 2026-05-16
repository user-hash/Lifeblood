#include "NativeGlobalEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"

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

    Symbol symbol;
    symbol.id = symbolId;
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "field";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = storage == CX_SC_Static ? "private" : "public";
    symbol.isStatic = storage == CX_SC_Static;
    symbol.properties["native.kind"] =
        existing != nullptr &&
        existing->properties.find("native.kind") != existing->properties.end() &&
        existing->properties.at("native.kind") == "callbackTable"
            ? "callbackTable"
            : "global";
    symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
    symbol.properties["native.fieldType"] = NormalizeTypeForId(
        ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
    symbol.properties["native.buildProfile"] = buildProfile_;
    if (symbol.properties["native.kind"] == "callbackTable")
        symbol.properties["native.callbackTable"] = "true";
    graph_.AddSymbol(symbol);

    types_.AddTypeReference(symbol.id, cursor, clang_getCursorType(cursor), "globalType");
    return true;
}

bool NativeGlobalEmitter::IsFileScopeCursor(CXCursor cursor) const
{
    CXCursor parent = clang_getCursorSemanticParent(cursor);
    return !clang_Cursor_isNull(parent) &&
           clang_getCursorKind(parent) == CXCursor_TranslationUnit;
}
}
