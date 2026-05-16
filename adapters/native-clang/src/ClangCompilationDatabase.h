#pragma once

#include <clang-c/CXCompilationDatabase.h>

#include <filesystem>

namespace lifeblood::native_clang
{
class ClangCompilationDatabase
{
public:
    explicit ClangCompilationDatabase(const std::filesystem::path& compilationDatabaseDir);
    ~ClangCompilationDatabase();

    ClangCompilationDatabase(const ClangCompilationDatabase&) = delete;
    ClangCompilationDatabase& operator=(const ClangCompilationDatabase&) = delete;

    ClangCompilationDatabase(ClangCompilationDatabase&&) = delete;
    ClangCompilationDatabase& operator=(ClangCompilationDatabase&&) = delete;

    bool IsValid() const { return valid_; }
    unsigned Count() const;
    CXCompileCommand CommandAt(unsigned index) const;

private:
    CXCompilationDatabase database_ = nullptr;
    CXCompileCommands commands_ = nullptr;
    bool valid_ = false;
};
}
