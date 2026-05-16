#pragma once

#include "NativeGraphSink.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeFileRegistry
{
public:
    NativeFileRegistry(
        std::string moduleName,
        std::string moduleId,
        std::string buildProfile,
        NativeGraphSink& graph);

    void EnsureFileSymbol(const std::string& relativePath);

private:
    std::string moduleName_;
    std::string moduleId_;
    std::string buildProfile_;
    NativeGraphSink& graph_;
};
}
