#include "NativeTypeMemberFactsCollector.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeTypeEmitter.h"

namespace lifeblood::native_clang
{
NativeTypeMemberFactsCollector::NativeTypeMemberFactsCollector(const ClangSourceMapper& sourceMap)
    : sourceMap_(sourceMap)
{
}

std::optional<NativeEnumConstantDeclarationFacts>
NativeTypeMemberFactsCollector::CollectEnumConstant(
    NativeCursorHandle handle,
    const std::string& enumTypeId) const
{
    auto cursor = handle.cursor;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return std::nullopt;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return std::nullopt;

    std::string enumName = OwnerNameFromTypeId(enumTypeId);

    NativeEnumConstantDeclarationFacts facts;
    facts.filePath = *file;
    facts.symbolId = "field:" + enumName + "." + name;
    facts.name = name;
    facts.qualifiedName = enumName + "." + name;
    facts.parentId = enumTypeId;
    facts.enumValue = std::to_string(clang_getEnumConstantDeclValue(cursor));
    facts.line = sourceMap_.Line(cursor);
    return facts;
}

std::optional<NativeFieldDeclarationFacts> NativeTypeMemberFactsCollector::CollectField(
    NativeCursorHandle handle,
    const std::string& ownerTypeId) const
{
    if (ownerTypeId.empty()) return std::nullopt;

    auto cursor = handle.cursor;
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return std::nullopt;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return std::nullopt;

    std::string owner = OwnerNameFromTypeId(ownerTypeId);

    NativeFieldDeclarationFacts facts;
    facts.filePath = *file;
    facts.symbolId = "field:" + owner + "." + name;
    facts.name = name;
    facts.qualifiedName = owner + "." + name;
    facts.parentId = ownerTypeId;
    facts.fieldType = NormalizeTypeForId(ToString(clang_getTypeSpelling(clang_getCursorType(cursor))));
    facts.line = sourceMap_.Line(cursor);
    return facts;
}

void NativeTypeMemberFactsCollector::AddFieldTypeReference(
    NativeCursorHandle handle,
    const std::string& fieldId,
    NativeTypeEmitter& types) const
{
    auto cursor = handle.cursor;
    types.AddTypeReference(
        fieldId,
        cursor,
        clang_getCursorType(cursor),
        NativeReferenceKinds::FieldType);
}
}
