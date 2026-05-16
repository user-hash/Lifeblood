#pragma once

#include <filesystem>
#include <string>

namespace lifeblood::native_clang
{
struct Options
{
    std::filesystem::path projectRoot;
    std::filesystem::path compilationDatabaseDir;
    std::filesystem::path outputPath;
    std::string profile = "default";
};
}
