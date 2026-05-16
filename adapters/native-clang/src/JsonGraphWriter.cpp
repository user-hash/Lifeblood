#include "JsonGraphWriter.h"

#include "JsonGraphElements.h"

#include <algorithm>
#include <ostream>
#include <tuple>
#include <vector>

namespace lifeblood::native_clang
{
void WriteJsonGraph(std::ostream& output, const NativeGraph& graph)
{
    output << "{\n";
    output << "  \"version\": \"1.0\",\n";
    output << "  \"language\": \"c\",\n";
    output << "  \"adapter\": {\n";
    output << "    \"name\": \"native-clang\",\n";
    output << "    \"version\": \"0.1.0-dev\",\n";
    output << "    \"capabilities\": {\n";
    output << "      \"discoverSymbols\": true,\n";
    output << "      \"typeResolution\": \"proven\",\n";
    output << "      \"callResolution\": \"proven\",\n";
    output << "      \"implementationResolution\": \"none\",\n";
    output << "      \"crossModuleReferences\": \"none\",\n";
    output << "      \"overrideResolution\": \"none\"\n";
    output << "    }\n";
    output << "  },\n";

    output << "  \"symbols\": [\n";
    bool first = true;
    for (const auto& [_, symbol] : graph.symbols)
    {
        if (!first) output << ",\n";
        first = false;
        WriteJsonSymbol(output, symbol);
    }
    output << "\n  ],\n";

    std::vector<Edge> edges = graph.edges;
    std::sort(edges.begin(), edges.end(), [](const Edge& a, const Edge& b) {
        return std::tie(a.sourceId, a.targetId, a.kind) <
               std::tie(b.sourceId, b.targetId, b.kind);
    });

    output << "  \"edges\": [\n";
    first = true;
    for (const auto& edge : edges)
    {
        if (!first) output << ",\n";
        first = false;
        WriteJsonEdge(output, edge);
    }
    output << "\n  ]\n";
    output << "}\n";
}
}
