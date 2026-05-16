#pragma once

#include <clang-c/Index.h>

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class ClangSourceMapper;
class NativeFileRegistry;
class NativeGraphSink;

class NativeMacroEmitter
{
public:
    NativeMacroEmitter(
        std::string moduleId,
        std::string buildProfile,
        NativeGraphSink& graph,
        const ClangSourceMapper& sourceMap,
        NativeFileRegistry& files);

    void AddMacroDefinition(CXCursor cursor, CXTranslationUnit unit);
    void AddMacroExpansion(CXCursor cursor);

private:
    void AddMacroSymbol(
        const std::string& name,
        const std::optional<std::string>& file,
        unsigned line,
        const std::string& source,
        const std::string& value);

    std::string MacroReplacement(CXCursor cursor, CXTranslationUnit unit) const;

    std::string moduleId_;
    std::string buildProfile_;
    NativeGraphSink& graph_;
    const ClangSourceMapper& sourceMap_;
    NativeFileRegistry& files_;
};
}
