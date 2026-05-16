#include "ClangCompilationDatabase.h"

#include <iostream>

namespace lifeblood::native_clang
{
ClangCompilationDatabase::ClangCompilationDatabase(
    const std::filesystem::path& compilationDatabaseDir)
{
    CXCompilationDatabase_Error error = CXCompilationDatabase_NoError;
    database_ = clang_CompilationDatabase_fromDirectory(
        compilationDatabaseDir.string().c_str(),
        &error);
    if (error != CXCompilationDatabase_NoError || database_ == nullptr)
    {
        std::cerr << "Failed to read compile_commands.json from "
                  << compilationDatabaseDir.string() << "\n";
        return;
    }

    commands_ = clang_CompilationDatabase_getAllCompileCommands(database_);
    if (Count() == 0)
    {
        std::cerr << "Compilation database contains no commands\n";
        return;
    }

    valid_ = true;
}

ClangCompilationDatabase::~ClangCompilationDatabase()
{
    if (commands_ != nullptr)
        clang_CompileCommands_dispose(commands_);
    if (database_ != nullptr)
        clang_CompilationDatabase_dispose(database_);
}

unsigned ClangCompilationDatabase::Count() const
{
    return commands_ == nullptr ? 0 : clang_CompileCommands_getSize(commands_);
}

CXCompileCommand ClangCompilationDatabase::CommandAt(unsigned index) const
{
    return clang_CompileCommands_getCommand(commands_, index);
}
}
