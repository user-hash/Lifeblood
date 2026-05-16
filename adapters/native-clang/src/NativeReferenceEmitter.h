#pragma once

#include "ClangSourceMapper.h"
#include "NativeDeclarationEmitter.h"
#include "NativeGraphSink.h"

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class NativeReferenceEmitter
{
public:
    NativeReferenceEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeDeclarationEmitter& declarations);

    void AddDirectCall(CXCursor cursor, const std::string& currentFunctionId);
    void AddDeclarationReference(
        CXCursor cursor,
        const std::string& currentFunctionId,
        const std::string& initializerOwnerId);
    void AddMemberReference(CXCursor cursor, const std::string& currentFunctionId);

private:
    void AddReferenceEdge(
        CXCursor cursor,
        const std::string& sourceId,
        const std::string& targetId,
        const std::string& referenceKind);

    void MarkCallbackTable(const std::string& symbolId);

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeDeclarationEmitter& declarations_;
};
}
