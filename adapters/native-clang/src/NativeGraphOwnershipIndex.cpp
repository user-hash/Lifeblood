#include "NativeGraphOwnershipIndex.h"

#include <set>

namespace lifeblood::native_clang
{
NativeGraphOwnershipIndex::NativeGraphOwnershipIndex(const NativeGraph& graph)
    : graph_(graph)
{
}

std::optional<std::string> NativeGraphOwnershipIndex::OwningModuleId(
    const std::string& symbolId) const
{
    std::set<std::string> visited;
    std::string currentId = symbolId;

    while (!currentId.empty() && visited.insert(currentId).second)
    {
        auto current = graph_.symbols.find(currentId);
        if (current == graph_.symbols.end())
            return std::nullopt;

        if (current->second.kind == "module")
            return currentId;

        currentId = current->second.parentId;
    }

    return std::nullopt;
}

std::optional<std::string> NativeGraphOwnershipIndex::OwningFileId(
    const std::string& symbolId) const
{
    auto symbol = graph_.symbols.find(symbolId);
    if (symbol == graph_.symbols.end())
        return std::nullopt;

    if (symbol->second.kind == "file")
        return symbolId;

    if (!symbol->second.filePath.empty())
        return "file:" + symbol->second.filePath;

    return std::nullopt;
}
}
