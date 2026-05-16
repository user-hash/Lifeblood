#include "ClangUtilities.h"
#include "ClangCompileCommandReader.h"
#include "ClangSourceMapper.h"
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
#include <vector>

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

        std::vector<const char*> cArgs;
        cArgs.reserve(compileCommand.parseArguments.size());
        for (const auto& arg : compileCommand.parseArguments)
            cArgs.push_back(arg.c_str());

        CXTranslationUnit unit = nullptr;
        const unsigned parseOptions = CXTranslationUnit_DetailedPreprocessingRecord;
        CXErrorCode parseResult = clang_parseTranslationUnit2(
            index,
            compileCommand.sourcePath.string().c_str(),
            cArgs.data(),
            static_cast<int>(cArgs.size()),
            nullptr,
            0,
            parseOptions,
            &unit);

        if (parseResult != CXError_Success || unit == nullptr)
        {
            std::cerr << "Failed to parse " << compileCommand.sourcePath.string()
                      << " (CXErrorCode " << parseResult << ")\n";
            return false;
        }

        const unsigned diagnosticCount = clang_getNumDiagnostics(unit);
        for (unsigned i = 0; i < diagnosticCount; i++)
        {
            CXDiagnostic diagnostic = clang_getDiagnostic(unit, i);
            auto severity = clang_getDiagnosticSeverity(diagnostic);
            if (severity >= CXDiagnostic_Error)
            {
                std::cerr << ToString(clang_formatDiagnostic(
                    diagnostic,
                    clang_defaultDiagnosticDisplayOptions())) << "\n";
            }
            clang_disposeDiagnostic(diagnostic);
        }

        astVisitor_.Visit(clang_getTranslationUnitCursor(unit), unit);

        clang_disposeTranslationUnit(unit);
        return true;
    }

    Options options_;
    fs::path projectRoot_;
    fs::path compilationDatabaseDir_;
    ClangCompileCommandReader commandReader_;
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
