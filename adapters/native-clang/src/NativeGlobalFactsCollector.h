#pragma once

#include "NativeCursorHandle.h"
#include "NativeGlobalDeclarationFacts.h"

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeTypeEmitter;

class NativeGlobalFactsCollector
{
public:
    explicit NativeGlobalFactsCollector(const ClangSourceMapper& sourceMap);

    std::optional<NativeGlobalDeclarationFacts> Collect(NativeCursorHandle cursor) const;

    void AddTypeReference(
        NativeCursorHandle cursor,
        const std::string& globalId,
        NativeTypeEmitter& types) const;

private:
    bool IsFileScopeCursor(NativeCursorHandle cursor) const;

    const ClangSourceMapper& sourceMap_;
};
}
