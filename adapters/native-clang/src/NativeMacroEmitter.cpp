#include "NativeMacroEmitter.h"

#include "ClangSourceMapper.h"
#include "ClangUtilities.h"
#include "NativeEvidenceKinds.h"
#include "NativeFileRegistry.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativeMacroSources.h"
#include "NativeReferenceKinds.h"
#include "NativeSymbolIds.h"
#include "NativeVisibilityNames.h"

#include <sstream>
#include <utility>

namespace lifeblood::native_clang
{
NativeMacroEmitter::NativeMacroEmitter(
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

void NativeMacroEmitter::AddMacroDefinition(CXCursor cursor, CXTranslationUnit unit)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return;

    AddMacroSymbol(
        name,
        file,
        sourceMap_.Line(cursor),
        NativeMacroSources::Source,
        MacroReplacement(cursor, unit));
}

void NativeMacroEmitter::AddMacroExpansion(CXCursor cursor)
{
    auto file = sourceMap_.SourceFile(cursor);
    if (!file) return;

    std::string name = ToString(clang_getCursorSpelling(cursor));
    if (name.empty()) return;

    std::string targetId = MacroId(name);
    if (!graph_.HasSymbol(targetId))
        AddMacroSymbol(name, std::nullopt, 0, NativeMacroSources::Unknown, "");

    files_.EnsureFileSymbol(*file);

    Edge edge;
    edge.sourceId = "file:" + *file;
    edge.targetId = targetId;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, NativeEvidenceKinds::Syntax);
    edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
    edge.properties[NativeGraphPropertyKeys::ReferenceKind] =
        NativeReferenceKinds::MacroExpansion;
    edge.properties[NativeGraphPropertyKeys::BuildProfile] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeMacroEmitter::AddMacroSymbol(
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
    symbol.visibility = NativeVisibilityNames::Internal;
    symbol.isStatic = true;
    symbol.properties[NativeGraphPropertyKeys::NativeKind] = NativeKindNames::Macro;
    symbol.properties["native.macroSource"] = source;
    symbol.properties["native.macroValue"] = value;
    symbol.properties[NativeGraphPropertyKeys::BuildProfile] = buildProfile_;
    graph_.AddSymbol(symbol);
}

std::string NativeMacroEmitter::MacroReplacement(
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
