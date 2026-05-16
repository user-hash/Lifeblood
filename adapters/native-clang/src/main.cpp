#include "JsonGraphWriter.h"
#include "LibClangExtractor.h"
#include "Options.h"

#include <filesystem>
#include <fstream>
#include <iostream>
#include <optional>
#include <string>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
namespace
{
void PrintUsage()
{
    std::cerr << "Usage: lifeblood-native-clang --project <path> "
              << "[--compilation-database <dir>] [--profile <name>] [--out <graph.json>]\n";
}

std::optional<Options> ParseArgs(int argc, char** argv)
{
    Options options;
    for (int i = 1; i < argc; i++)
    {
        std::string arg = argv[i];
        auto requireValue = [&](const std::string& name) -> std::optional<std::string> {
            if (i + 1 >= argc)
            {
                std::cerr << name << " requires a value\n";
                return std::nullopt;
            }
            return std::string(argv[++i]);
        };

        if (arg == "--project")
        {
            auto value = requireValue(arg);
            if (!value) return std::nullopt;
            options.projectRoot = *value;
        }
        else if (arg == "--compilation-database")
        {
            auto value = requireValue(arg);
            if (!value) return std::nullopt;
            options.compilationDatabaseDir = *value;
        }
        else if (arg == "--profile")
        {
            auto value = requireValue(arg);
            if (!value) return std::nullopt;
            options.profile = *value;
        }
        else if (arg == "--out")
        {
            auto value = requireValue(arg);
            if (!value) return std::nullopt;
            options.outputPath = *value;
        }
        else if (arg == "--help" || arg == "-h")
        {
            PrintUsage();
            return std::nullopt;
        }
        else if (options.projectRoot.empty())
        {
            options.projectRoot = arg;
        }
        else
        {
            std::cerr << "Unknown argument: " << arg << "\n";
            return std::nullopt;
        }
    }

    if (options.projectRoot.empty())
    {
        PrintUsage();
        return std::nullopt;
    }

    if (options.compilationDatabaseDir.empty())
        options.compilationDatabaseDir = options.projectRoot;

    return options;
}

int Run(int argc, char** argv)
{
    auto options = ParseArgs(argc, argv);
    if (!options) return 1;

    LibClangExtractor extractor(*options);
    if (!extractor.Run()) return 1;

    if (!options->outputPath.empty())
    {
        std::ofstream output(options->outputPath);
        if (!output)
        {
            std::cerr << "Failed to open output path: " << options->outputPath.string() << "\n";
            return 1;
        }
        WriteJsonGraph(output, extractor.Graph());
    }
    else
    {
        WriteJsonGraph(std::cout, extractor.Graph());
    }

    return 0;
}
}
}

int main(int argc, char** argv)
{
    return lifeblood::native_clang::Run(argc, argv);
}
