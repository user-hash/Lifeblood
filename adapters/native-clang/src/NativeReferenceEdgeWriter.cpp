#include "NativeReferenceEdgeWriter.h"

#include "ClangSourceMapper.h"
#include "NativeCallKinds.h"
#include "NativeEvidenceKinds.h"
#include "NativeGraphMetricPropertyKeys.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativePropertyWriter.h"

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
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::CallKind, NativeCallKinds::Direct);
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::RecordDirectCallCounts(
    const std::string& sourceId,
    const std::string& targetId)
{
    directCallCounts_.Record(sourceId, targetId);
    graph_.UpdateSymbol(sourceId, [&](Symbol& symbol) {
        directCallCounts_.Decorate(
            symbol,
            NativeGraphMetricPropertyKeys::DirectCallInCount,
            NativeGraphMetricPropertyKeys::DirectCallOutCount);
    });

    graph_.UpdateSymbol(targetId, [&](Symbol& symbol) {
        directCallCounts_.Decorate(
            symbol,
            NativeGraphMetricPropertyKeys::DirectCallInCount,
            NativeGraphMetricPropertyKeys::DirectCallOutCount);
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
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::ReferenceKind, referenceKind);
    NativePropertyWriter::Set(edge, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddEdge(edge);
}

void NativeReferenceEdgeWriter::MarkCallbackTable(const std::string& symbolId)
{
    graph_.UpdateSymbol(symbolId, [](Symbol& symbol) {
        NativePropertyWriter::Set(
            symbol,
            NativeGraphPropertyKeys::NativeKind,
            NativeKindNames::CallbackTable);
        NativePropertyWriter::SetTrue(symbol, NativeGraphPropertyKeys::CallbackTable);
    });
}
}
