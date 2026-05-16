#include "NativeReferenceEdgeWriter.h"

#include "ClangSourceMapper.h"
#include "NativeGraphSink.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeReferenceEdgeWriter::NativeReferenceEdgeWriter(
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      sourceMap_(sourceMap)
{
}

void NativeReferenceEdgeWriter::AddDirectCall(
    CXCursor cursor,
    const std::string& sourceId,
    const std::string& targetId)
{
    Edge edge;
    edge.sourceId = sourceId;
    edge.targetId = targetId;
    edge.kind = "calls";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
    edge.callSite = sourceMap_.CallSiteFor(cursor, sourceId);
    edge.properties["native.callKind"] = "direct";
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::AddReference(
    CXCursor cursor,
    const std::string& sourceId,
    const std::string& targetId,
    const std::string& referenceKind)
{
    Edge edge;
    edge.sourceId = sourceId;
    edge.targetId = targetId;
    edge.kind = "references";
    edge.evidence = sourceMap_.EvidenceFor(cursor, "semantic");
    edge.callSite = sourceMap_.CallSiteFor(cursor, sourceId);
    edge.properties["native.referenceKind"] = referenceKind;
    edge.properties["native.buildProfile"] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::MarkCallbackTable(const std::string& symbolId)
{
    graph_.UpdateSymbol(symbolId, [](Symbol& symbol) {
        symbol.properties["native.kind"] = "callbackTable";
        symbol.properties["native.callbackTable"] = "true";
    });
}
}
