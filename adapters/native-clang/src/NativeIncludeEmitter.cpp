#include "NativeIncludeEmitter.h"

#include "ClangSourceMapper.h"
#include "NativeEvidenceKinds.h"
#include "NativeFileRegistry.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"

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
    RecordIncludeCounts(*sourceFile, *includedPath);

    Edge edge;
    edge.sourceId = "file:" + *sourceFile;
    edge.targetId = "file:" + *includedPath;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, NativeEvidenceKinds::Syntax);
    edge.callSite = sourceMap_.CallSiteFor(cursor, edge.sourceId);
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::NativeKind, NativeKindNames::Include);
    NativePropertyWriter::Set(
        edge,
        NativeGraphPropertyKeys::Include,
        fs::path(*includedPath).filename().string());
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddEdge(edge);
}

void NativeIncludeEmitter::RecordIncludeCounts(
    const std::string& sourceFile,
    const std::string& includedFile)
{
    const auto sourceCount = ++includeDirectiveCounts_[sourceFile];
    graph_.UpdateSymbol("file:" + sourceFile, [&](Symbol& file) {
        NativePropertyWriter::SetCount(
            file,
            NativeGraphPropertyKeys::IncludeDirectiveCount,
            sourceCount);
    });

    const auto includedByCount = ++includedByCounts_[includedFile];
    graph_.UpdateSymbol("file:" + includedFile, [&](Symbol& file) {
        NativePropertyWriter::SetCount(
            file,
            NativeGraphPropertyKeys::IncludedByCount,
            includedByCount);
    });
}
}
