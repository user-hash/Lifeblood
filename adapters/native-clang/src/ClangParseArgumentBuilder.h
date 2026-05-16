#pragma once

#include <clang-c/CXCompilationDatabase.h>

#include <filesystem>
#include <string>
#include <vector>

namespace lifeblood::native_clang
{
class ClangParseArgumentBuilder
{
public:
    std::vector<std::string> Build(
        CXCompileCommand command,
        const std::filesystem::path& sourcePath,
        const std::filesystem::path& commandDirectory) const;

private:
    bool IsSourceArgument(
        const std::string& arg,
        const std::filesystem::path& sourcePath,
        const std::filesystem::path& commandDirectory) const;

    std::string NormalizePathArgument(
        const std::string& arg,
        const std::filesystem::path& commandDirectory) const;
};
}
