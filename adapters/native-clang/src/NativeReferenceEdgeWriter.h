#pragma once

#include <clang-c/Index.h>

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

    void MarkCallbackTable(const std::string& symbolId);

private:
    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
};
}
