#include "ClangBuildArgumentFactsCollector.h"

namespace lifeblood::native_clang
{
void ClangBuildArgumentFactsCollector::Collect(NativeCompileCommand& command) const
{
    command.sourceLanguage = SourceLanguage(command.sourcePath);

    for (std::size_t i = 0; i < command.parseArguments.size(); i++)
    {
        const std::string& arg = command.parseArguments[i];
        if (arg == "-I")
        {
            command.includeSearchPathCount++;
            i++;
            continue;
        }
        if (arg.rfind("-I", 0) == 0 && arg.size() > 2)
        {
            command.includeSearchPathCount++;
            continue;
        }

        if (arg == "-isystem")
        {
            command.systemIncludeSearchPathCount++;
            i++;
            continue;
        }
        if (arg.rfind("-isystem", 0) == 0 && arg.size() > 8)
        {
            command.systemIncludeSearchPathCount++;
            continue;
        }

        if (arg == "-iquote")
        {
            command.quoteIncludeSearchPathCount++;
            i++;
            continue;
        }
        if (arg.rfind("-iquote", 0) == 0 && arg.size() > 7)
        {
            command.quoteIncludeSearchPathCount++;
            continue;
        }

        if (arg.rfind("-std=", 0) == 0)
            command.languageStandard = arg.substr(5);
    }
}

std::string ClangBuildArgumentFactsCollector::SourceLanguage(
    const std::filesystem::path& sourcePath)
{
    std::string extension = sourcePath.extension().string();
    if (extension == ".c")
        return "c";
    if (extension == ".cc" || extension == ".cpp" || extension == ".cxx" || extension == ".C")
        return "c++";
    if (extension == ".s" || extension == ".S" || extension == ".asm")
        return "assembly";

    return "unknown";
}
}
