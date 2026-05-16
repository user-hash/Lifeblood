#include "ClangCompileCommandReader.h"

#include "ClangBuildArgumentFactsCollector.h"
#include "ClangCommandLineMacroCollector.h"
#include "ClangParseArgumentBuilder.h"
#include "ClangUtilities.h"

#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
ClangCompileCommandReader::ClangCompileCommandReader(fs::path compilationDatabaseDir)
    : compilationDatabaseDir_(std::move(compilationDatabaseDir))
{
}

NativeCompileCommand ClangCompileCommandReader::Read(CXCompileCommand command) const
{
    NativeCompileCommand result;

    result.directory = fs::path(ToString(clang_CompileCommand_getDirectory(command)));
    if (!result.directory.is_absolute())
        result.directory = compilationDatabaseDir_ / result.directory;
    result.directory = fs::weakly_canonical(result.directory);

    auto file = fs::path(ToString(clang_CompileCommand_getFilename(command)));
    result.sourcePath = file.is_absolute() ? file : result.directory / file;
    result.sourcePath = fs::weakly_canonical(result.sourcePath);

    result.parseArguments = ClangParseArgumentBuilder().Build(
        command,
        result.sourcePath,
        result.directory);
    ClangCommandLineMacroCollector().Collect(command, result);
    ClangBuildArgumentFactsCollector().Collect(result);
    return result;
}
}
