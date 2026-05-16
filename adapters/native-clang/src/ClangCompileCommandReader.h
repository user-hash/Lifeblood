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
    std::filesystem::path compilationDatabaseDir_;
};
}
