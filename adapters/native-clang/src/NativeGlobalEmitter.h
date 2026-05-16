#pragma once

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeFileRegistry;
class NativeGraphSink;
class NativeTypeEmitter;

class NativeGlobalEmitter
{
public:
    NativeGlobalEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files,
        NativeTypeEmitter& types);

    bool AddGlobalVariable(CXCursor cursor);

private:
    bool IsFileScopeCursor(CXCursor cursor) const;

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
    NativeTypeEmitter& types_;
};
}
