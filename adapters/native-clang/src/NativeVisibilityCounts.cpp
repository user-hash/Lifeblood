#include "NativeVisibilityCounts.h"

namespace lifeblood::native_clang
{
void NativeVisibilityCounter::Add(NativeVisibilityCounts& counts, const Symbol& symbol)
{
    if (symbol.visibility == "public")
        counts.publicCount++;
    else if (symbol.visibility == "private")
        counts.privateCount++;
    else
        counts.internalCount++;
}

void NativeVisibilityCounter::Write(
    Symbol& symbol,
    const NativeVisibilityCounts& counts,
    const std::string& publicProperty,
    const std::string& privateProperty,
    const std::string& internalProperty)
{
    symbol.properties[publicProperty] = std::to_string(counts.publicCount);
    symbol.properties[privateProperty] = std::to_string(counts.privateCount);
    symbol.properties[internalProperty] = std::to_string(counts.internalCount);
}
}
