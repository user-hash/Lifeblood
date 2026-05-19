#include "NativeFunctionFactsCollector.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"

#include <sstream>

namespace lifeblood::native_clang
{
NativeFunctionFactsCollector::NativeFunctionFactsCollector(const ClangSourceMapper& sourceMap)
    : sourceMap_(sourceMap)
{
}

std::optional<NativeFunctionDeclarationFacts> NativeFunctionFactsCollector::Collect(
    NativeCursorHandle handle) const
{
    auto cursor = handle.cursor;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return std::nullopt;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return std::nullopt;

    const auto storage = clang_Cursor_getStorageClass(cursor);
    NativeFunctionDeclarationFacts facts;
    facts.filePath = *file;
    facts.symbolId = FunctionId(cursor);
    facts.name = name;
    facts.signature = Signature(handle);
    facts.line = sourceMap_.Line(cursor);
    facts.isDefinition = clang_isCursorDefinition(cursor);
    facts.isStatic = storage == CX_SC_Static;
    return facts;
}

void NativeFunctionFactsCollector::AddTypeReferences(
    NativeCursorHandle handle,
    const std::string& functionId,
    NativeTypeEmitter& types) const
{
    auto cursor = handle.cursor;
    AddParameterTypeReferences(handle, functionId, types);
    types.AddTypeReference(
        functionId,
        cursor,
        clang_getCursorResultType(cursor),
        NativeReferenceKinds::ReturnType);
}

void NativeFunctionFactsCollector::AddParameterTypeReferences(
    NativeCursorHandle handle,
    const std::string& functionId,
    NativeTypeEmitter& types) const
{
    auto cursor = handle.cursor;
    const int count = clang_Cursor_getNumArguments(cursor);
    for (int i = 0; i < count; i++)
    {
        auto arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
        types.AddTypeReference(
            functionId,
            arg,
            clang_getCursorType(arg),
            NativeReferenceKinds::ParameterType);
    }
}

std::string NativeFunctionFactsCollector::Signature(NativeCursorHandle handle) const
{
    auto cursor = handle.cursor;
    std::ostringstream signature;
    signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorResultType(cursor))));
    signature << " (";
    const int count = clang_Cursor_getNumArguments(cursor);
    for (int i = 0; i < count; i++)
    {
        if (i > 0) signature << ", ";
        auto arg = clang_Cursor_getArgument(cursor, static_cast<unsigned>(i));
        signature << NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(arg))));
    }
    signature << ")";
    return signature.str();
}
}
