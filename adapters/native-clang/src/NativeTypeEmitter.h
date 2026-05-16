#pragma once

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class NativeTypeEmitter
{
public:
    NativeTypeEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files);

    bool AddRecordType(CXCursor cursor, const std::string& nativeKind);
    bool AddTypedefType(CXCursor cursor);
    void AddTypeReference(
        const std::string& sourceId,
        CXCursor evidenceCursor,
        CXType sourceType,
        const std::string& referenceKind);

private:
    bool EnsureTypeDeclaration(CXCursor declaration, CXType type);
    std::string NativeKindForType(CXType type) const;

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
};
}
