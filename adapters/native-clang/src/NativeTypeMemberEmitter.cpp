#include "NativeTypeMemberEmitter.h"

#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"
#include "NativeVisibilityNames.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeTypeMemberEmitter::NativeTypeMemberEmitter(
    std::string buildProfile,
    NativeGraphSink& graph)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph)
{
}

bool NativeTypeMemberEmitter::AddEnumConstant(
    const NativeEnumConstantDeclarationFacts& facts)
{
    if (facts.filePath.empty() ||
        facts.symbolId.empty() ||
        facts.name.empty() ||
        facts.parentId.empty())
        return false;

    Symbol symbol;
    symbol.id = facts.symbolId;
    symbol.name = facts.name;
    symbol.qualifiedName = facts.qualifiedName;
    symbol.kind = "field";
    symbol.filePath = facts.filePath;
    symbol.line = facts.line;
    symbol.parentId = facts.parentId;
    symbol.visibility = NativeVisibilityNames::Public;
    symbol.isStatic = true;
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        NativeKindNames::EnumMember);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::EnumValue,
        facts.enumValue);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(symbol);
    return true;
}

bool NativeTypeMemberEmitter::AddField(const NativeFieldDeclarationFacts& facts)
{
    if (facts.filePath.empty() ||
        facts.symbolId.empty() ||
        facts.name.empty() ||
        facts.parentId.empty())
        return false;

    Symbol field;
    field.id = facts.symbolId;
    field.name = facts.name;
    field.qualifiedName = facts.qualifiedName;
    field.kind = "field";
    field.filePath = facts.filePath;
    field.line = facts.line;
    field.parentId = facts.parentId;
    field.visibility = NativeVisibilityNames::Public;
    NativePropertyWriter::Set(field, NativeGraphPropertyKeys::NativeKind, NativeKindNames::StructField);
    NativePropertyWriter::Set(
        field,
        NativeGraphPropertyKeys::FieldType,
        facts.fieldType);
    NativePropertyWriter::Set(field, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(field);

    return true;
}
}
