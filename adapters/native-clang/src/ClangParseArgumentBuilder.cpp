#include "ClangParseArgumentBuilder.h"

#include "ClangUtilities.h"

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
std::vector<std::string> ClangParseArgumentBuilder::Build(
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

bool ClangParseArgumentBuilder::IsSourceArgument(
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

std::string ClangParseArgumentBuilder::NormalizePathArgument(
    const std::string& arg,
    const fs::path& commandDirectory) const
{
    fs::path path(arg);
    if (path.is_absolute())
        return SlashPath(path.string());

    return SlashPath((commandDirectory / path).lexically_normal().string());
}
}
