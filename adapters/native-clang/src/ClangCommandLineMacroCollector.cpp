#include "ClangCommandLineMacroCollector.h"

#include "ClangUtilities.h"

namespace lifeblood::native_clang
{
void ClangCommandLineMacroCollector::Collect(
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

void ClangCommandLineMacroCollector::AddDefine(
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

void ClangCommandLineMacroCollector::AddUndefine(
    const std::string& name,
    NativeCompileCommand& result) const
{
    if (!name.empty())
        result.undefines.push_back(name);
}
}
