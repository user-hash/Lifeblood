#include "ClangCompileCommandReader.h"

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

    result.parseArguments = BuildParseArguments(command, result.sourcePath, result.directory);
    CollectMacros(command, result);
    return result;
}

std::vector<std::string> ClangCompileCommandReader::BuildParseArguments(
    CXCompileCommand command,
    const fs::path& sourcePath,
    const fs::path& commandDirectory) const
{
    std::vector<std::string> args;
    const unsigned count = clang_CompileCommand_getNumArgs(command);
    for (unsigned i = 1; i < count; i++)
    {
        std::string arg = ToString(clang_CompileCommand_getArg(command, i));
        if (arg == "-c") continue;
        if (arg == "-o")
        {
            i++;
            continue;
        }

        if (arg == "-I" || arg == "-iquote" || arg == "-isystem")
        {
            args.push_back(arg);
            if (i + 1 < count)
                args.push_back(NormalizePathArgument(
                    ToString(clang_CompileCommand_getArg(command, ++i)),
                    commandDirectory));
            continue;
        }

        if (arg.rfind("-I", 0) == 0 && arg.size() > 2)
        {
            args.push_back("-I" + NormalizePathArgument(arg.substr(2), commandDirectory));
            continue;
        }

        if (IsSourceArgument(arg, sourcePath, commandDirectory))
            continue;

        args.push_back(arg);
    }
    return args;
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

bool ClangCompileCommandReader::IsSourceArgument(
    const std::string& arg,
    const fs::path& sourcePath,
    const fs::path& commandDirectory) const
{
    fs::path maybePath(arg);
    if (maybePath.extension() != sourcePath.extension())
        return false;

    if (!maybePath.is_absolute())
        maybePath = commandDirectory / maybePath;

    std::error_code ec;
    auto canonical = fs::weakly_canonical(maybePath, ec);
    return !ec && canonical == sourcePath;
}

std::string ClangCompileCommandReader::NormalizePathArgument(
    const std::string& arg,
    const fs::path& commandDirectory) const
{
    fs::path path(arg);
    if (path.is_absolute())
        return SlashPath(path.string());

    return SlashPath((commandDirectory / path).lexically_normal().string());
}
}
