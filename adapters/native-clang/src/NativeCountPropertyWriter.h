#pragma once

#include "GraphModel.h"
#include "NativePropertyWriter.h"

#include <array>
#include <cstddef>

namespace lifeblood::native_clang
{
template <typename Counts>
struct NativeCountProperty
{
    const char* property;
    unsigned Counts::* value;
};

template <typename Counts, std::size_t Count>
void WriteNativeCountProperties(
    Symbol& symbol,
    const Counts& counts,
    const std::array<NativeCountProperty<Counts>, Count>& countProperties)
{
    for (const auto& countProperty : countProperties)
        NativePropertyWriter::SetCount(
            symbol,
            countProperty.property,
            counts.*countProperty.value);
}
}
