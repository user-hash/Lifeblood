#include "NativeDirectionalSymbolCounts.h"

#include "NativePropertyWriter.h"

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
        NativePropertyWriter::SetCount(symbol, outgoingProperty, outgoing->second);

    auto incoming = incomingCounts_.find(symbol.id);
    if (incoming != incomingCounts_.end())
        NativePropertyWriter::SetCount(symbol, incomingProperty, incoming->second);
}
}
