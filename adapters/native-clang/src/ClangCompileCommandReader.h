#pragma once

#include "NativeCompileCommand.h"

#include <clang-c/CXCompilationDatabase.h>

#include <filesystem>
#include <string>
#include <vector>

namespace lifeblood::native_clang
{
class ClangCompileCommandReader
{
public:
    explicit ClangCompileCommandReader(std::filesystem::path compilationDatabaseDir);

    NativeCompileCommand Read(CXCompileCommand command) const;

private:
    void CollectMacros(CXCompileCommand command, NativeCompileCommand& result) const;
    void AddDefine(const std::string& raw, NativeCompileCommand& result) const;
    void AddUndefine(const std::string& name, NativeCompileCommand& result) const;

    std::filesystem::path compilationDatabaseDir_;
};
}
