#include "NativeReferenceEdgeWriter.h"

#include "ClangSourceMapper.h"
#include "NativeCallKinds.h"
#include "NativeEvidenceKinds.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"

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
    RecordDirectCallCounts(sourceId, targetId);

    Edge edge;
    edge.sourceId = sourceId;
    edge.targetId = targetId;
    edge.kind = "calls";
    edge.evidence = sourceMap_.EvidenceFor(cursor, NativeEvidenceKinds::Semantic);
    edge.callSite = sourceMap_.CallSiteFor(cursor, sourceId);
    edge.properties[NativeGraphPropertyKeys::CallKind] = NativeCallKinds::Direct;
    edge.properties[NativeGraphPropertyKeys::BuildProfile] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::RecordDirectCallCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    const auto outCount = ++directCallOutCounts_[sourceId];
    graph_.UpdateSymbol(sourceId, [&](Symbol& symbol) {
        symbol.properties["native.directCallOutCount"] = std::to_string(outCount);
    });

    const auto inCount = ++directCallInCounts_[targetId];
    graph_.UpdateSymbol(targetId, [&](Symbol& symbol) {
        symbol.properties["native.directCallInCount"] = std::to_string(inCount);
    });
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
    edge.evidence = sourceMap_.EvidenceFor(cursor, NativeEvidenceKinds::Semantic);
    edge.callSite = sourceMap_.CallSiteFor(cursor, sourceId);
    edge.properties[NativeGraphPropertyKeys::ReferenceKind] = referenceKind;
    edge.properties[NativeGraphPropertyKeys::BuildProfile] = buildProfile_;
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::MarkCallbackTable(const std::string& symbolId)
{
    graph_.UpdateSymbol(symbolId, [](Symbol& symbol) {
        symbol.properties[NativeGraphPropertyKeys::NativeKind] =
            NativeKindNames::CallbackTable;
        symbol.properties["native.callbackTable"] = "true";
    });
}
}
