#include "NativeVisibilityCounts.h"

#include "NativePropertyWriter.h"
#include "NativeVisibilityNames.h"

namespace lifeblood::native_clang
{
void NativeVisibilityCounter::Add(NativeVisibilityCounts& counts, const Symbol& symbol)
{
    if (symbol.visibility == NativeVisibilityNames::Public)
        counts.publicCount++;
    else if (symbol.visibility == NativeVisibilityNames::Private)
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
    NativePropertyWriter::SetCount(symbol, publicProperty, counts.publicCount);
    NativePropertyWriter::SetCount(symbol, privateProperty, counts.privateCount);
    NativePropertyWriter::SetCount(symbol, internalProperty, counts.internalCount);
}
}
