#pragma once

#include "GraphModel.h"
#include "Options.h"

#include <set>
#include <string>
#include <tuple>

namespace lifeblood::native_clang
{
class LibClangExtractor
{
public:
    explicit LibClangExtractor(Options options);

    bool Run();

    const NativeGraph& Graph() const { return graph_; }

private:
    Options options_;
    NativeGraph graph_;
    std::set<std::tuple<std::string, std::string, std::string>> edgeKeys_;
};
}
