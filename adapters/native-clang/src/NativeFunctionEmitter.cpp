#include "NativeFunctionEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"

#include <sstream>
#include <utility>

namespace lifeblood::native_clang
{
NativeFunctionEmitter::NativeFunctionEmitter(
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

bool NativeFunctionEmitter::AddFunction(CXCursor cursor)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;

    files_.EnsureFileSymbol(*file);

    const bool isDefinition = clang_isCursorDefinition(cursor);
    const auto storage = clang_Cursor_getStorageClass(cursor);
    Symbol symbol;
    symbol.id = FunctionId(cursor);
    if (ExistingDefinitionShouldWin(symbol.id, isDefinition)) return true;

    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "method";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = storage == CX_SC_Static ? "private" : "public";
    symbol.isStatic = storage == CX_SC_Static;
    symbol.properties["native.kind"] = "function";
    symbol.properties["native.declarationKind"] = isDefinition ? "definition" : "declaration";
    symbol.properties["native.linkage"] = storage == CX_SC_Static ? "internal" : "external";
    symbol.properties["native.signature"] = Signature(cursor);
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);

    AddParameterTypeReferences(cursor, symbol.id);
    types_.AddTypeReference(symbol.id, cursor, clang_getCursorResultType(cursor), "returnType");
    return true;
}

bool NativeFunctionEmitter::ExistingDefinitionShouldWin(
    const std::string& symbolId,
    bool isDefinition) const
{
    if (isDefinition) return false;

    const Symbol* existing = graph_.FindSymbol(symbolId);
    if (existing == nullptr) return false;

    auto it = existing->properties.find("native.declarationKind");
    return it != existing->properties.end() && it->second == "definition";
}

void NativeFunctionEmitter::AddParameterTypeReferences(
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

std::string NativeFunctionEmitter::Signature(CXCursor cursor) const
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
