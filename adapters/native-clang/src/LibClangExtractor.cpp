#include "ClangUtilities.h"
#include "ClangCompileCommandReader.h"
#include "ClangSourceMapper.h"
#include "ClangTranslationUnitParser.h"
#include "LibClangExtractor.h"
#include "NativeAstVisitor.h"
#include "NativeDeclarationEmitter.h"
#include "NativeFileRegistry.h"
#include "NativeGraphBuilder.h"
#include "NativeGraphSink.h"
#include "NativeModuleTracker.h"
#include "NativePreprocessorEmitter.h"
#include "NativeReferenceEmitter.h"

#include <clang-c/CXCompilationDatabase.h>
#include <clang-c/Index.h>

#include <filesystem>
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

class ExtractionSession
{
public:
    ExtractionSession(
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

    bool Run()
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

private:
    fs::path ResolvePath(const fs::path& path)
    {
        fs::path resolved = path;
        if (!resolved.is_absolute())
            resolved = fs::absolute(resolved);

        std::error_code ec;
        auto canonical = fs::weakly_canonical(resolved, ec);
        return ec ? resolved.lexically_normal() : canonical;
    }

    bool ParseCommand(CXIndex index, CXCompileCommand command)
    {
        NativeCompileCommand compileCommand = commandReader_.Read(command);
        module_.BeginTranslationUnit(compileCommand);

        auto unit = unitParser_.Parse(index, compileCommand);
        if (!unit) return false;

        astVisitor_.Visit(clang_getTranslationUnitCursor(unit.Get()), unit.Get());
        return true;
    }

    Options options_;
    fs::path projectRoot_;
    fs::path compilationDatabaseDir_;
    ClangCompileCommandReader commandReader_;
    ClangTranslationUnitParser unitParser_;
    ClangSourceMapper sourceMap_;
    NativeGraphSink& graph_;
    NativeModuleTracker module_;
    NativeFileRegistry files_;
    NativeDeclarationEmitter declarations_;
    NativeReferenceEmitter references_;
    NativePreprocessorEmitter preprocessor_;
    NativeAstVisitor astVisitor_;
};
}

LibClangExtractor::LibClangExtractor(Options options)
    : options_(std::move(options))
{
}

bool LibClangExtractor::Run()
{
    NativeGraphBuilder graphBuilder(graph_);
    graphBuilder.Clear();

    ExtractionSession session(options_, graphBuilder);
    return session.Run();
}
}
