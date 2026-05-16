#pragma once

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeFunctionEmitter.h"
#include "NativeGlobalEmitter.h"
#include "NativeGraphSink.h"
#include "NativeTypeEmitter.h"
#include "NativeTypeMemberEmitter.h"

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
    NativeTypeEmitter types_;
    NativeTypeMemberEmitter typeMembers_;
    NativeGlobalEmitter globals_;
    NativeFunctionEmitter functions_;
};
}
