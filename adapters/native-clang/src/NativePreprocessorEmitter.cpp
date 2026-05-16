#include "NativePreprocessorEmitter.h"

#include "ClangUtilities.h"
#include "NativeSymbolIds.h"

#include <filesystem>
#include <optional>
#include <sstream>
#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
NativePreprocessorEmitter::NativePreprocessorEmitter(
    std::string moduleId,
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files)
    : moduleId_(std::move(moduleId)),
      buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap),
      files_(files)
{
}

void NativePreprocessorEmitter::AddInclude(CXCursor cursor)
{
    auto sourceFile = sourceMap_.SourceFile(cursor);
    if (!sourceFile) return;

    CXFile included = clang_getIncludedFile(cursor);
    if (included == nullptr) return;
    auto includedPath = sourceMap_.RelativePath(included);
    if (!includedPath) return;

    files_.EnsureFileSymbol(*sourceFile);
    files_.EnsureFileSymbol(*includedPath);

    Edge edge;
    edge.sourceId = "file:" + *sourceFile;
    edge.targetId = "file:" + *includedPath;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "syntax");
    edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
    edge.properties["native.kind"] = "include";
    edge.properties["native.include"] = fs::path(*includedPath).filename().string();
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativePreprocessorEmitter::AddMacroDefinition(CXCursor cursor, CXTranslationUnit unit)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return;

    AddMacroSymbol(name, file, sourceMap_.Line(cursor), "source", MacroReplacement(cursor, unit));
}

void NativePreprocessorEmitter::AddMacroExpansion(CXCursor cursor)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return;

    std::string targetId = MacroId(name);
    if (!graph_.HasSymbol(targetId))
        AddMacroSymbol(name, std::nullopt, 0, "unknown", "");

    files_.EnsureFileSymbol(*file);

    Edge edge;
    edge.sourceId = "file:" + *file;
    edge.targetId = targetId;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "syntax");
    edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
    edge.properties["native.referenceKind"] = "macroExpansion";
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativePreprocessorEmitter::AddMacroSymbol(
    const std::string& name,
    const std::optional<std::string>& file,
    unsigned line,
    const std::string& source,
    const std::string& value)
{
    Symbol symbol;
    symbol.id = MacroId(name);
    symbol.name = name;
    symbol.qualifiedName = name;
    symbol.kind = "field";
    if (file)
    {
        files_.EnsureFileSymbol(*file);
        symbol.filePath = *file;
        symbol.line = line;
        symbol.parentId = "file:" + *file;
    }
    else
    {
        symbol.parentId = moduleId_;
    }
    symbol.visibility = "internal";
    symbol.isStatic = true;
    symbol.properties["native.kind"] = "macro";
    symbol.properties["native.macroSource"] = source;
    symbol.properties["native.macroValue"] = value;
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);
}

std::string NativePreprocessorEmitter::MacroReplacement(
    CXCursor cursor,
    CXTranslationUnit unit) const
{
    if (unit == nullptr) return "";

    CXToken* tokens = nullptr;
    unsigned tokenCount = 0;
    clang_tokenize(unit, clang_getCursorExtent(cursor), &tokens, &tokenCount);
    if (tokens == nullptr || tokenCount <= 1)
    {
        if (tokens != nullptr)
            clang_disposeTokens(unit, tokens, tokenCount);
        return "";
    }

    std::ostringstream value;
    for (unsigned i = 1; i < tokenCount; i++)
    {
        if (i > 1) value << ' ';
        value << ToString(clang_getTokenSpelling(unit, tokens[i]));
    }

    clang_disposeTokens(unit, tokens, tokenCount);
    return value.str();
}
}
