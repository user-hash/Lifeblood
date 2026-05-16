#include "NativeDeclarationEmitter.h"

#include "ClangUtilities.h"
#include "NativeSymbolIds.h"

#include <sstream>
#include <utility>

namespace lifeblood::native_clang
{
NativeDeclarationEmitter::NativeDeclarationEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap),
      files_(files),
      types_(buildProfile_, graph_, sourceMap_, files_)
{
}

bool NativeDeclarationEmitter::AddRecordType(CXCursor cursor, const std::string& nativeKind)
{
    return types_.AddRecordType(cursor, nativeKind);
}

bool NativeDeclarationEmitter::AddTypedefType(CXCursor cursor)
{
    return types_.AddTypedefType(cursor);
}

bool NativeDeclarationEmitter::AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
{
    return types_.AddEnumConstant(cursor, enumTypeId);
}

void NativeDeclarationEmitter::AddField(CXCursor cursor, const std::string& ownerTypeId)
{
    types_.AddField(cursor, ownerTypeId);
}

bool NativeDeclarationEmitter::AddGlobalVariable(CXCursor cursor)
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

bool NativeDeclarationEmitter::AddFunction(CXCursor cursor)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;

    files_.EnsureFileSymbol(*file);

    const auto storage = clang_Cursor_getStorageClass(cursor);
    Symbol symbol;
    symbol.id = FunctionId(cursor);
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "method";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = storage == CX_SC_Static ? "private" : "public";
    symbol.isStatic = storage == CX_SC_Static;
    symbol.properties["native.kind"] = "function";
    symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
    symbol.properties["native.signature"] = Signature(cursor);
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);

    AddParameterTypeReferences(cursor, symbol.id);
    types_.AddTypeReference(symbol.id, cursor, clang_getCursorResultType(cursor), "returnType");
    return true;
}

void NativeDeclarationEmitter::AddParameterTypeReferences(
    CXCursor cursor,
    const std::string& functionId)
{
    const int count = clang_Cursor_getNumArguments(cursor);
    for (int i = 0; i < count; i++)
    {
        CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
        types_.AddTypeReference(functionId, arg, clang_getCursorType(arg), "parameterType");
    }
}

bool NativeDeclarationEmitter::IsFileScopeCursor(CXCursor cursor) const
{
    CXCursor parent = clang_getCursorSemanticParent(cursor);
    return !clang_Cursor_isNull(parent) &&
           clang_getCursorKind(parent) == CXCursor_TranslationUnit;
}

std::string NativeDeclarationEmitter::Signature(CXCursor cursor) const
{
    std::ostringstream signature;
    signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorResultType(cursor))));
    signature << " (";
    const int count = clang_Cursor_getNumArguments(cursor);
    for (int i = 0; i < count; i++)
    {
        if (i > 0) signature << ", ";
        CXCursor arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
        signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(arg))));
    }
    signature << ")";
    return signature.str();
}
}
