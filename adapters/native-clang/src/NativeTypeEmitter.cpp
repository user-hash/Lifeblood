#include "NativeTypeEmitter.h"

#include "ClangUtilities.h"
#include "NativeSymbolIds.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeTypeEmitter::NativeTypeEmitter(
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

bool NativeTypeEmitter::AddRecordType(CXCursor cursor, const std::string& nativeKind)
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

bool NativeTypeEmitter::AddTypedefType(CXCursor cursor)
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

bool NativeTypeEmitter::AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
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

void NativeTypeEmitter::AddField(CXCursor cursor, const std::string& ownerTypeId)
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

void NativeTypeEmitter::AddTypeReference(
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

bool NativeTypeEmitter::EnsureTypeDeclaration(CXCursor declaration, CXType type)
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

std::string NativeTypeEmitter::NativeKindForType(CXType type) const
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
}
