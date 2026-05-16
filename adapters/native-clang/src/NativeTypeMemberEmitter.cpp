#include "NativeTypeMemberEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeFileRegistry.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"
#include "NativeVisibilityNames.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeTypeMemberEmitter::NativeTypeMemberEmitter(
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

bool NativeTypeMemberEmitter::AddEnumConstant(CXCursor cursor, const std::string& enumTypeId)
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
    symbol.visibility = NativeVisibilityNames::Public;
    symbol.isStatic = true;
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        NativeKindNames::EnumMember);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::EnumValue,
        std::to_string(clang_getEnumConstantDeclValue(cursor)));
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(symbol);
    return true;
}

void NativeTypeMemberEmitter::AddField(CXCursor cursor, const std::string& ownerTypeId)
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
    field.visibility = NativeVisibilityNames::Public;
    NativePropertyWriter::Set(field, NativeGraphPropertyKeys::NativeKind, NativeKindNames::StructField);
    NativePropertyWriter::Set(
        field,
        NativeGraphPropertyKeys::FieldType,
        NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(cursor)))));
    NativePropertyWriter::Set(field, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(field);

    types_.AddTypeReference(
        field.id,
        cursor,
        clang_getCursorType(cursor),
        NativeReferenceKinds::FieldType);
}
}
