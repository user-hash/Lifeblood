#pragma once

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeFileRegistry;
class NativeGraphSink;
class NativeTypeEmitter;

class NativeFunctionEmitter
{
public:
    NativeFunctionEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files,
        NativeTypeEmitter& types);

    bool AddFunction(CXCursor cursor);

private:
    bool ExistingDefinitionShouldWin(const std::string& symbolId, bool isDefinition) const;
    void AddParameterTypeReferences(CXCursor cursor, const std::string& functionId);
    std::string Signature(CXCursor cursor) const;

    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
    NativeTypeEmitter& types_;
};
}
