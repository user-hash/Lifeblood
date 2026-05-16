#include "LibClangExtractor.h"

#include "NativeExtractionSession.h"
#include "NativeGraphBuilder.h"

#include <utility>

namespace lifeblood::native_clang
{
LibClangExtractor::LibClangExtractor(Options options)
    : options_(std::move(options))
{
}

bool LibClangExtractor::Run()
{
    NativeGraphBuilder graphBuilder(graph_);
    graphBuilder.Clear();

    NativeExtractionSession session(options_, graphBuilder);
    return session.Run();
}
}
