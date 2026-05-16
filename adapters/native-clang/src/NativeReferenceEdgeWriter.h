#pragma once

#include "NativeDirectionalSymbolCounts.h"
#include "NativeTableRowEmitter.h"

#include <clang-c/Index.h>

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeGraphSink;

class NativeReferenceEdgeWriter
{
public:
    NativeReferenceEdgeWriter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap);

    void AddDirectCall(
        CXCursor cursor,
        const std::string& sourceId,
        const std::string& targetId);

    void AddReference(
        CXCursor cursor,
        const std::string& sourceId,
        const std::string& targetId,
        const std::string& referenceKind);

    void AddCallbackTarget(
        CXCursor cursor,
        const std::string& tableId,
        std::optional<unsigned> rowOrdinal,
        const std::string& targetId);

    void MarkCallbackTable(const std::string& symbolId);

private:
    void RecordDirectCallCounts(const std::string& sourceId, const std::string& targetId);

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeDirectionalSymbolCounts directCallCounts_;
    NativeTableRowEmitter tableRows_;
};
}
