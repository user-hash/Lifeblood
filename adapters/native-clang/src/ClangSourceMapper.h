#pragma once

#include "GraphModel.h"

#include <clang-c/Index.h>

#include <filesystem>
#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper
{
public:
    explicit ClangSourceMapper(std::filesystem::path projectRoot);

    std::optional<std::string> SourceFile(CXCursor cursor) const;
    std::optional<std::string> RelativePath(CXFile file) const;
    std::optional<std::string> RelativePath(const std::filesystem::path& input) const;

    unsigned Line(CXCursor cursor) const;
    Evidence EvidenceFor(CXCursor cursor, const std::string& kind) const;
    std::optional<CallSite> CallSiteFor(
        CXCursor cursor,
        const std::string& containingSymbolId) const;

private:
    std::filesystem::path projectRoot_;
};
}
