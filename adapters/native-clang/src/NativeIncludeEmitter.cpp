#include "NativeIncludeEmitter.h"

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"

#include <filesystem>
#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
NativeIncludeEmitter::NativeIncludeEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap),
      files_(files)
{
}

void NativeIncludeEmitter::AddInclude(CXCursor cursor)
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
}
