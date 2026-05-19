#pragma once

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeFunctionEmitter.h"
#include "NativeFunctionFactsCollector.h"
#include "NativeGlobalEmitter.h"
#include "NativeGlobalFactsCollector.h"
#include "NativeGraphSink.h"
#include "NativeTypeEmitter.h"
#include "NativeTypeMemberEmitter.h"
#include "NativeCursorHandle.h"

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

    bool AddRecordType(NativeCursorHandle cursor, const std::string& nativeKind);
    bool AddTypedefType(NativeCursorHandle cursor);
    bool AddEnumConstant(NativeCursorHandle cursor, const std::string& enumTypeId);
    void AddField(NativeCursorHandle cursor, const std::string& ownerTypeId);
    bool AddGlobalVariable(NativeCursorHandle cursor);
    bool AddFunction(NativeCursorHandle cursor);

private:
    NativeTypeEmitter types_;
    NativeTypeMemberEmitter typeMembers_;
    NativeGlobalFactsCollector globalFacts_;
    NativeGlobalEmitter globals_;
    NativeFunctionFactsCollector functionFacts_;
    NativeFunctionEmitter functions_;
};
}
