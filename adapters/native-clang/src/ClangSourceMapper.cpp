#include "ClangSourceMapper.h"

#include "ClangUtilities.h"

#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
ClangSourceMapper::ClangSourceMapper(fs::path projectRoot)
    : projectRoot_(std::move(projectRoot))
{
}

std::optional<std::string> ClangSourceMapper::SourceFile(CXCursor cursor) const
{
    CXSourceLocation location = clang_getCursorLocation(cursor);
    CXFile file = nullptr;
    unsigned line = 0, column = 0, offset = 0;
    clang_getSpellingLocation(location, &file, &line, &column, &offset);
    if (file == nullptr) return std::nullopt;

    return RelativePath(file);
}

std::optional<std::string> ClangSourceMapper::RelativePath(CXFile file) const
{
    fs::path path = ToString(clang_getFileName(file));
    if (path.empty()) return std::nullopt;

    return RelativePath(path);
}

std::optional<std::string> ClangSourceMapper::RelativePath(const fs::path& input) const
{
    fs::path path = input;
    if (!path.is_absolute())
        path = projectRoot_ / path;

    std::error_code ec;
    fs::path canonical = fs::weakly_canonical(path, ec);
    if (ec) canonical = fs::absolute(path, ec);
    if (ec) return std::nullopt;

    auto rel = fs::relative(canonical, projectRoot_, ec);
    if (ec || rel.empty()) return std::nullopt;
    auto text = SlashPath(rel.generic_string());
    if (text.rfind("..", 0) == 0) return std::nullopt;
    return text;
}

unsigned ClangSourceMapper::Line(CXCursor cursor) const
{
    CXSourceLocation location = clang_getCursorLocation(cursor);
    CXFile file = nullptr;
    unsigned line = 0, column = 0, offset = 0;
    clang_getSpellingLocation(location, &file, &line, &column, &offset);
    return line;
}

Evidence ClangSourceMapper::EvidenceFor(CXCursor cursor, const std::string& kind) const
{
    Evidence evidence;
    evidence.kind = kind;
    auto file = SourceFile(cursor);
    if (file)
        evidence.sourceSpan = *file + ":" + std::to_string(Line(cursor));
    return evidence;
}

std::optional<CallSite> ClangSourceMapper::CallSiteFor(
    CXCursor cursor,
    const std::string& containingSymbolId) const
{
    CXSourceRange range = clang_getCursorExtent(cursor);
    CXSourceLocation start = clang_getRangeStart(range);
    CXSourceLocation end = clang_getRangeEnd(range);

    CXFile startFile = nullptr;
    unsigned startLine = 0, startColumn = 0, startOffset = 0;
    clang_getSpellingLocation(start, &startFile, &startLine, &startColumn, &startOffset);
    if (startFile == nullptr) return std::nullopt;

    auto rel = RelativePath(startFile);
    if (!rel) return std::nullopt;

    CXFile endFile = nullptr;
    unsigned endLine = 0, endColumn = 0, endOffset = 0;
    clang_getSpellingLocation(end, &endFile, &endLine, &endColumn, &endOffset);

    CallSite site;
    site.filePath = *rel;
    site.line = startLine;
    site.column = startColumn;
    site.endLine = endLine;
    site.endColumn = endColumn;
    site.containingSymbolId = containingSymbolId;
    return site;
}
}
