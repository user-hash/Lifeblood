#pragma once

#include <filesystem>
#include <string>
#include <vector>

namespace lifeblood::native_clang
{
struct CommandLineDefine
{
    std::string name;
    std::string value;
};

struct NativeCompileCommand
{
    std::filesystem::path directory;
    std::filesystem::path sourcePath;
    std::vector<std::string> parseArguments;
    std::vector<CommandLineDefine> defines;
    std::vector<std::string> undefines;
    unsigned includeSearchPathCount = 0;
    unsigned systemIncludeSearchPathCount = 0;
    unsigned quoteIncludeSearchPathCount = 0;
    std::string languageStandard;
    std::string sourceLanguage;
};
}
