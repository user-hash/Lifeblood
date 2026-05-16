#pragma once

#include "GraphModel.h"

#include <set>
#include <string>
#include <tuple>

namespace lifeblood::native_clang
{
class NativeGraphBuilder
{
public:
    explicit NativeGraphBuilder(NativeGraph& graph);

    void Clear();

    void AddSymbol(Symbol symbol);
    void AddEdge(Edge edge);

    bool HasSymbol(const std::string& symbolId) const;
    Symbol* FindSymbol(const std::string& symbolId);
    const Symbol* FindSymbol(const std::string& symbolId) const;

private:
    NativeGraph& graph_;
    std::set<std::tuple<std::string, std::string, std::string>> edgeKeys_;
};
}
