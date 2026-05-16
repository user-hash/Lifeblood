#pragma once

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeFileRegistry;
class NativeGraphSink;
class NativeTypeEmitter;

class NativeTypeMemberEmitter
{
public:
    NativeTypeMemberEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files,
        NativeTypeEmitter& types);

    bool AddEnumConstant(CXCursor cursor, const std::string& enumTypeId);
    void AddField(CXCursor cursor, const std::string& ownerTypeId);

private:
    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
    NativeTypeEmitter& types_;
};
}
