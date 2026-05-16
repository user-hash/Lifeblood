#pragma once

#include <clang-c/CXCompilationDatabase.h>

#include <filesystem>
#include <string>
#include <vector>

namespace lifeblood::native_clang
{
struct CommandLineDefine
{
    std::string name;
    std::string value;
};

struct NativeCompileCommand
{
    std::filesystem::path directory;
    std::filesystem::path sourcePath;
    std::vector<std::string> parseArguments;
    std::vector<CommandLineDefine> defines;
    std::vector<std::string> undefines;
};

class ClangCompileCommandReader
{
public:
    explicit ClangCompileCommandReader(std::filesystem::path compilationDatabaseDir);

    NativeCompileCommand Read(CXCompileCommand command) const;

private:
    std::vector<std::string> BuildParseArguments(
        CXCompileCommand command,
        const std::filesystem::path& sourcePath,
        const std::filesystem::path& commandDirectory) const;

    void CollectMacros(CXCompileCommand command, NativeCompileCommand& result) const;
    void AddDefine(const std::string& raw, NativeCompileCommand& result) const;
    void AddUndefine(const std::string& name, NativeCompileCommand& result) const;

    bool IsSourceArgument(
        const std::string& arg,
        const std::filesystem::path& sourcePath,
        const std::filesystem::path& commandDirectory) const;

    std::string NormalizePathArgument(
        const std::string& arg,
        const std::filesystem::path& commandDirectory) const;

    std::filesystem::path compilationDatabaseDir_;
};
}
