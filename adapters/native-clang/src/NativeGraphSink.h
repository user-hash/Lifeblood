#pragma once

#include "GraphModel.h"

#include <functional>
#include <string>

namespace lifeblood::native_clang
{
class NativeGraphSink
{
public:
    virtual ~NativeGraphSink() = default;

    virtual void AddSymbol(Symbol symbol) = 0;
    virtual void AddEdge(Edge edge) = 0;

    virtual bool HasSymbol(const std::string& symbolId) const = 0;
    virtual const Symbol* FindSymbol(const std::string& symbolId) const = 0;
    virtual void UpdateSymbol(
        const std::string& symbolId,
        const std::function<void(Symbol&)>& update) = 0;
};
}
