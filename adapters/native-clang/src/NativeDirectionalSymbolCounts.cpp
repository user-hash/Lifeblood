#include "NativeDirectionalSymbolCounts.h"

namespace lifeblood::native_clang
{
void NativeDirectionalSymbolCounts::Clear()
{
    outgoingCounts_.clear();
    incomingCounts_.clear();
}

void NativeDirectionalSymbolCounts::Record(
    const std::string& sourceId,
    const std::string& targetId)
{
    outgoingCounts_[sourceId]++;
    incomingCounts_[targetId]++;
}

void NativeDirectionalSymbolCounts::Decorate(
    Symbol& symbol,
    const std::string& incomingProperty,
    const std::string& outgoingProperty) const
{
    auto outgoing = outgoingCounts_.find(symbol.id);
    if (outgoing != outgoingCounts_.end())
        symbol.properties[outgoingProperty] = std::to_string(outgoing->second);

    auto incoming = incomingCounts_.find(symbol.id);
    if (incoming != incomingCounts_.end())
        symbol.properties[incomingProperty] = std::to_string(incoming->second);
}
}
