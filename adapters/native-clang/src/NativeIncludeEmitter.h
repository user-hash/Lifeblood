#pragma once

#include <clang-c/Index.h>

#include <map>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeFileRegistry;
class NativeGraphSink;

class NativeIncludeEmitter
{
public:
    NativeIncludeEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files);

    void AddInclude(CXCursor cursor);

private:
    void RecordIncludeCounts(const std::string& sourceFile, const std::string& includedFile);

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
    std::map<std::string, unsigned> includeDirectiveCounts_;
    std::map<std::string, unsigned> includedByCounts_;
};
}
