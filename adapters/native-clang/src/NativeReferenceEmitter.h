#pragma once

#include "ClangSourceMapper.h"
#include "NativeDeclarationEmitter.h"
#include "NativeGraphSink.h"
#include "NativeReferenceEdgeWriter.h"

#include <clang-c/Index.h>

#include <optional>
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
        const std::string& initializerOwnerId,
        std::optional<unsigned> initializerRowOrdinal);
    void AddMemberReference(CXCursor cursor, const std::string& currentFunctionId);

private:
    NativeReferenceEdgeWriter edges_;
    NativeDeclarationEmitter& declarations_;
};
}
