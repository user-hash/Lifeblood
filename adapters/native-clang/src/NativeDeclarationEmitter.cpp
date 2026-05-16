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
      files_(files)
{
}

bool NativeDeclarationEmitter::AddRecordType(CXCursor cursor, const std::string& nativeKind)
{
    if (!clang_isCursorDefinition(cursor)) return false;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;

    files_.EnsureFileSymbol(*file);

    Symbol symbol;
    symbol.id = TypeId(cursor);
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "type";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = "public";
    symbol.properties["native.kind"] = nativeKind;
    symbol.properties["native.linkage"] = "none";
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);
    return true;
}

bool NativeDeclarationEmitter::AddTypedefType(CXCursor cursor)
{
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;

    files_.EnsureFileSymbol(*file);

    Symbol symbol;
    symbol.id = TypeId(cursor);
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "type";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = "file:" + *file;
    symbol.visibility = "public";
    symbol.properties["native.kind"] = "typedef";
    symbol.properties["native.underlyingType"] = NormalizeTypeForId(
        ToString(clang_getTypeSpelling(clang_getTypedefDeclUnderlyingType(cursor))));
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);

    AddTypeReference(symbol.id, cursor, clang_getTypedefDeclUnderlyingType(cursor), "underlyingType");
    return true;
}

bool NativeDeclarationEmitter::AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return false;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return false;

    std::string enumName = OwnerNameFromTypeId(enumTypeId);

    Symbol symbol;
    symbol.id = "field:" + enumName + "." + name;
    symbol.name = name;
    symbol.qualifiedName = enumName + "." + name;
    symbol.kind = "field";
    symbol.filePath = *file;
    symbol.line = sourceMap_.Line(cursor);
    symbol.parentId = enumTypeId;
    symbol.visibility = "public";
    symbol.isStatic = true;
    symbol.properties["native.kind"] = "enumMember";
    symbol.properties["native.enumValue"] = std::to_string(clang_getEnumConstantDeclValue(cursor));
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);
    return true;
}

void NativeDeclarationEmitter::AddField(CXCursor cursor, const std::string& ownerTypeId)
{
    if (ownerTypeId.empty()) return;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return;
    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return;

    std::string owner = OwnerNameFromTypeId(ownerTypeId);

    Symbol field;
    field.id = "field:" + owner + "." + name;
    field.name = name;
    field.qualifiedName = owner + "." + name;
    field.kind = "field";
    field.filePath = *file;
    field.line = sourceMap_.Line(cursor);
    field.parentId = ownerTypeId;
    field.visibility = "public";
    field.properties["native.kind"] = "structField";
    field.properties["native.fieldType"] = NormalizeTypeForId(
        ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
    field.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(field);

    AddTypeReference(field.id, cursor, clang_getCursorType(cursor), "fieldType");
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

    AddTypeReference(symbol.id, cursor, clang_getCursorType(cursor), "globalType");
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
    AddTypeReference(symbol.id, cursor, clang_getCursorResultType(cursor), "returnType");
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
        AddTypeReference(functionId, arg, clang_getCursorType(arg), "parameterType");
    }
}

void NativeDeclarationEmitter::AddTypeReference(
    const std::string& sourceId,
    CXCursor evidenceCursor,
    CXType sourceType,
    const std::string& referenceKind)
{
    CXType type = StripPointers(sourceType);
    CXCursor declaration = clang_getTypeDeclaration(type);
    if (clang_Cursor_isNull(declaration)) return;

    if (!EnsureTypeDeclaration(declaration, type)) return;

    Edge edge;
    edge.sourceId = sourceId;
    edge.targetId = TypeId(declaration);
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(evidenceCursor, "semantic");
    edge.callSite = sourceMap_.CallSiteFor(evidenceCursor, sourceId);
    edge.properties["native.referenceKind"] = referenceKind;
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

bool NativeDeclarationEmitter::EnsureTypeDeclaration(CXCursor declaration, CXType type)
{
    switch (clang_getCursorKind(declaration))
    {
        case CXCursor_StructDecl:
        case CXCursor_UnionDecl:
            return AddRecordType(declaration, NativeKindForType(type));
        case CXCursor_EnumDecl:
            return AddRecordType(declaration, "enum");
        case CXCursor_TypedefDecl:
            return AddTypedefType(declaration);
        default:
            return false;
    }
}

bool NativeDeclarationEmitter::IsFileScopeCursor(CXCursor cursor) const
{
    CXCursor parent = clang_getCursorSemanticParent(cursor);
    return !clang_Cursor_isNull(parent) &&
           clang_getCursorKind(parent) == CXCursor_TranslationUnit;
}

std::string NativeDeclarationEmitter::NativeKindForType(CXType type) const
{
    switch (type.kind)
    {
        case CXType_Record:
            return "struct";
        case CXType_Enum:
            return "enum";
        default:
            return "type";
    }
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
