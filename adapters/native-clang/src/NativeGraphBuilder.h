#pragma once

#include "GraphModel.h"
#include "NativeGraphSink.h"

#include <map>
#include <set>
#include <string>
#include <tuple>

namespace lifeblood::native_clang
{
class NativeGraphBuilder : public NativeGraphSink
{
public:
    explicit NativeGraphBuilder(NativeGraph& graph);

    void Clear();

    void AddSymbol(Symbol symbol) override;
    void AddEdge(Edge edge) override;

    bool HasSymbol(const std::string& symbolId) const override;
    const Symbol* FindSymbol(const std::string& symbolId) const override;
    void UpdateSymbol(
        const std::string& symbolId,
        const std::function<void(Symbol&)>& update) override;

private:
    void RecordReferenceCounts(const std::string& sourceId, const std::string& targetId);

    NativeGraph& graph_;
    std::set<std::tuple<std::string, std::string, std::string>> edgeKeys_;
    std::map<std::string, unsigned> referenceOutCounts_;
    std::map<std::string, unsigned> referenceInCounts_;
};
}
