#pragma once

#include "GraphModel.h"

#include <map>
#include <string>

namespace lifeblood::native_clang
{
class NativeDirectionalSymbolCounts
{
public:
    void Clear();
    void Record(const std::string& sourceId, const std::string& targetId);
    void Decorate(
        Symbol& symbol,
        const std::string& incomingProperty,
        const std::string& outgoingProperty) const;

private:
    std::map<std::string, unsigned> outgoingCounts_;
    std::map<std::string, unsigned> incomingCounts_;
};
}
