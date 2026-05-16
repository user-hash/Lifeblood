#pragma once

#include "ClangSourceMapper.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"
#include "NativeIncludeEmitter.h"
#include "NativeMacroEmitter.h"

#include <clang-c/Index.h>

#include <string>

namespace lifeblood::native_clang
{
class NativePreprocessorEmitter
{
public:
    NativePreprocessorEmitter(
        std::string moduleId,
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files);

    void AddInclude(CXCursor cursor);
    void AddMacroDefinition(CXCursor cursor, CXTranslationUnit unit);
    void AddMacroExpansion(CXCursor cursor);

private:
    NativeIncludeEmitter includes_;
    NativeMacroEmitter macros_;
};
}
