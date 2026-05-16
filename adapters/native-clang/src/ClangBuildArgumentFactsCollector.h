#pragma once

#include "NativeCompileCommand.h"

namespace lifeblood::native_clang
{
class ClangBuildArgumentFactsCollector
{
public:
    void Collect(NativeCompileCommand& command) const;

private:
    static std::string SourceLanguage(const std::filesystem::path& sourcePath);
};
}
