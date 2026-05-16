#pragma once

#include "ClangCompileCommandReader.h"
#include "ClangSourceMapper.h"
#include "ClangTranslationUnitParser.h"
#include "NativeAstVisitor.h"
#include "NativeDeclarationEmitter.h"
#include "NativeFileRegistry.h"
#include "NativeGraphSink.h"
#include "NativeModuleTracker.h"
#include "NativePreprocessorEmitter.h"
#include "NativeReferenceEmitter.h"
#include "Options.h"

#include <clang-c/CXCompilationDatabase.h>
#include <clang-c/Index.h>

#include <filesystem>

namespace lifeblood::native_clang
{
class NativeExtractionSession
{
public:
    NativeExtractionSession(Options options, NativeGraphSink& graph);

    bool Run();

private:
    std::filesystem::path ResolvePath(const std::filesystem::path& path) const;
    bool ParseCommand(CXIndex index, CXCompileCommand command);

    Options options_;
    std::filesystem::path projectRoot_;
    std::filesystem::path compilationDatabaseDir_;
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
