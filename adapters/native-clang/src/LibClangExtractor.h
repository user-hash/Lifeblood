#pragma once

#include "GraphModel.h"
#include "Options.h"

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
};
}
