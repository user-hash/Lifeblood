#pragma once

#include "NativeCursorHandle.h"
#include "NativeFunctionDeclarationFacts.h"

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeTypeEmitter;

class NativeFunctionFactsCollector
{
public:
    explicit NativeFunctionFactsCollector(const ClangSourceMapper& sourceMap);

    std::optional<NativeFunctionDeclarationFacts> Collect(NativeCursorHandle cursor) const;

    void AddTypeReferences(
        NativeCursorHandle cursor,
        const std::string& functionId,
        NativeTypeEmitter& types) const;

private:
    void AddParameterTypeReferences(
        NativeCursorHandle cursor,
        const std::string& functionId,
        NativeTypeEmitter& types) const;

    std::string Signature(NativeCursorHandle cursor) const;

    const ClangSourceMapper& sourceMap_;
};
}
