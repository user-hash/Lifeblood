#include "NativePreprocessorEmitter.h"

#include <utility>

namespace lifeblood::native_clang
{
NativePreprocessorEmitter::NativePreprocessorEmitter(
    std::string moduleId,
    std::string buildProfile,
    NativeGraphSink& graph,
    const ClangSourceMapper& sourceMap,
    NativeFileRegistry& files)
    : includes_(buildProfile, graph, sourceMap, files),
      macros_(std::move(moduleId), std::move(buildProfile), graph, sourceMap, files)
{
}

void NativePreprocessorEmitter::AddInclude(CXCursor cursor)
{
    includes_.AddInclude(cursor);
}

void NativePreprocessorEmitter::AddMacroDefinition(CXCursor cursor, CXTranslationUnit unit)
{
    macros_.AddMacroDefinition(cursor, unit);
}

void NativePreprocessorEmitter::AddMacroExpansion(CXCursor cursor)
{
    macros_.AddMacroExpansion(cursor);
}
}
