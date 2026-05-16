#include "NativeGraphFinalizer.h"

namespace lifeblood::native_clang
{
void NativeGraphFinalizer::Finalize(NativeGraph& graph) const
{
    NativeGraphOwnershipIndex ownership(graph);
    NativeModuleGraphMetrics moduleMetrics(graph, ownership);
    NativeFileGraphMetrics fileMetrics(graph, ownership);

    for (const auto& [id, symbol] : graph.symbols)
    {
        moduleMetrics.ObserveSymbol(id, symbol);
        fileMetrics.ObserveSymbol(id, symbol);
    }

    for (const auto& edge : graph.edges)
    {
        moduleMetrics.ObserveEdge(edge);
        fileMetrics.ObserveEdge(edge);
    }

    fileMetrics.Write();
    moduleMetrics.Write();
}
}
