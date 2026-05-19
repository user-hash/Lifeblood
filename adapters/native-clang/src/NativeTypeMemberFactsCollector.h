#pragma once

#include "NativeCursorHandle.h"
#include "NativeTypeMemberDeclarationFacts.h"

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeTypeEmitter;

class NativeTypeMemberFactsCollector
{
public:
    explicit NativeTypeMemberFactsCollector(const ClangSourceMapper& sourceMap);

    std::optional<NativeEnumConstantDeclarationFacts> CollectEnumConstant(
        NativeCursorHandle cursor,
        const std::string& enumTypeId) const;

    std::optional<NativeFieldDeclarationFacts> CollectField(
        NativeCursorHandle cursor,
        const std::string& ownerTypeId) const;

    void AddFieldTypeReference(
        NativeCursorHandle cursor,
        const std::string& fieldId,
        NativeTypeEmitter& types) const;

private:
    const ClangSourceMapper& sourceMap_;
};
}
