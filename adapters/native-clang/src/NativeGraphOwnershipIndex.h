#pragma once

#include "GraphModel.h"

#include <optional>
#include <string>

namespace lifeblood::native_clang
{
class NativeGraphOwnershipIndex
{
public:
    explicit NativeGraphOwnershipIndex(const NativeGraph& graph);

    std::optional<std::string> OwningModuleId(const std::string& symbolId) const;
    std::optional<std::string> OwningFileId(const std::string& symbolId) const;

private:
    const NativeGraph& graph_;
};
}
