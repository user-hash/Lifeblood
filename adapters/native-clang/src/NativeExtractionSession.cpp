#include "NativeExtractionSession.h"

#include <iostream>
#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
namespace
{
std::string BaseName(const fs::path& path)
{
    auto name = path.filename().string();
    return name.empty() ? "native-project" : name;
}
}

NativeExtractionSession::NativeExtractionSession(
    Options options,
    NativeGraphSink& graph)
    : options_(std::move(options)),
      projectRoot_(fs::weakly_canonical(options_.projectRoot)),
      compilationDatabaseDir_(ResolvePath(options_.compilationDatabaseDir)),
      commandReader_(compilationDatabaseDir_),
      unitParser_(),
      sourceMap_(projectRoot_),
      graph_(graph),
      module_(BaseName(projectRoot_), options_.profile, graph_),
      files_(module_.ModuleName(), module_.ModuleId(), options_.profile, graph_),
      declarations_(options_.profile, graph_, sourceMap_, files_),
      references_(options_.profile, graph_, sourceMap_, declarations_),
      preprocessor_(module_.ModuleId(), options_.profile, graph_, sourceMap_, files_),
      astVisitor_(declarations_, references_, preprocessor_)
{
}

bool NativeExtractionSession::Run()
{
    CXCompilationDatabase_Error error = CXCompilationDatabase_NoError;
    CXCompilationDatabase database = clang_CompilationDatabase_fromDirectory(
        compilationDatabaseDir_.string().c_str(),
        &error);
    if (error != CXCompilationDatabase_NoError || database == nullptr)
    {
        std::cerr << "Failed to read compile_commands.json from "
                  << compilationDatabaseDir_.string() << "\n";
        return false;
    }

    CXCompileCommands commands = clang_CompilationDatabase_getAllCompileCommands(database);
    const unsigned commandCount = clang_CompileCommands_getSize(commands);
    if (commandCount == 0)
    {
        std::cerr << "Compilation database contains no commands\n";
        clang_CompileCommands_dispose(commands);
        clang_CompilationDatabase_dispose(database);
        return false;
    }

    CXIndex index = clang_createIndex(/*excludeDeclarationsFromPCH*/ 0, /*displayDiagnostics*/ 0);
    bool ok = true;
    for (unsigned i = 0; i < commandCount; i++)
    {
        CXCompileCommand command = clang_CompileCommands_getCommand(commands, i);
        ok = ParseCommand(index, command) && ok;
    }

    clang_disposeIndex(index);
    clang_CompileCommands_dispose(commands);
    clang_CompilationDatabase_dispose(database);
    return ok;
}

fs::path NativeExtractionSession::ResolvePath(const fs::path& path) const
{
    fs::path resolved = path;
    if (!resolved.is_absolute())
        resolved = fs::absolute(resolved);

    std::error_code ec;
    auto canonical = fs::weakly_canonical(resolved, ec);
    return ec ? resolved.lexically_normal() : canonical;
}

bool NativeExtractionSession::ParseCommand(CXIndex index, CXCompileCommand command)
{
    NativeCompileCommand compileCommand = commandReader_.Read(command);
    module_.BeginTranslationUnit(compileCommand);

    auto unit = unitParser_.Parse(index, compileCommand);
    if (!unit) return false;

    astVisitor_.Visit(clang_getTranslationUnitCursor(unit.Get()), unit.Get());
    return true;
}
}
