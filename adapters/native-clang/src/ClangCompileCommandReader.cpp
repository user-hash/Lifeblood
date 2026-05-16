#include "ClangCompileCommandReader.h"

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
    CollectMacros(command, result);
    return result;
}

void ClangCompileCommandReader::CollectMacros(
    CXCompileCommand command,
    NativeCompileCommand& result) const
{
    const unsigned count = clang_CompileCommand_getNumArgs(command);
    for (unsigned i = 1; i < count; i++)
    {
        std::string arg = ToString(clang_CompileCommand_getArg(command, i));
        if (arg == "-D")
        {
            if (i + 1 < count)
                AddDefine(ToString(clang_CompileCommand_getArg(command, ++i)), result);
            continue;
        }

        if (arg.rfind("-D", 0) == 0 && arg.size() > 2)
        {
            AddDefine(arg.substr(2), result);
            continue;
        }

        if (arg == "-U")
        {
            if (i + 1 < count)
                AddUndefine(ToString(clang_CompileCommand_getArg(command, ++i)), result);
            continue;
        }

        if (arg.rfind("-U", 0) == 0 && arg.size() > 2)
            AddUndefine(arg.substr(2), result);
    }
}

void ClangCompileCommandReader::AddDefine(
    const std::string& raw,
    NativeCompileCommand& result) const
{
    if (raw.empty()) return;

    auto equal = raw.find('=');
    std::string name = equal == std::string::npos ? raw : raw.substr(0, equal);
    std::string value = equal == std::string::npos ? "1" : raw.substr(equal + 1);
    if (name.empty()) return;

    result.defines.push_back(CommandLineDefine{ name, value });
}

void ClangCompileCommandReader::AddUndefine(
    const std::string& name,
    NativeCompileCommand& result) const
{
    if (!name.empty())
        result.undefines.push_back(name);
}

}
