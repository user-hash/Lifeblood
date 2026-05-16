#pragma once

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class NativeDeclarationEmitter
{
public:
    NativeDeclarationEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files);

    bool AddRecordType(CXCursor cursor, const std::string& nativeKind);
    bool AddTypedefType(CXCursor cursor);
    bool AddEnumConstant(CXCursor cursor, const std::string& enumTypeId);
    void AddField(CXCursor cursor, const std::string& ownerTypeId);
    bool AddGlobalVariable(CXCursor cursor);
    bool AddFunction(CXCursor cursor);

private:
    void AddParameterTypeReferences(CXCursor cursor, const std::string& functionId);
    void AddTypeReference(
        const std::string& sourceId,
        CXCursor evidenceCursor,
        CXType sourceType,
        const std::string& referenceKind);

    bool EnsureTypeDeclaration(CXCursor declaration, CXType type);
    bool IsFileScopeCursor(CXCursor cursor) const;

    std::string NativeKindForType(CXType type) const;
    std::string Signature(CXCursor cursor) const;

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
};
}
