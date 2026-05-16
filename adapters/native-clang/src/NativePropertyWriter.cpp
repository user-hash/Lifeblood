#include "NativePropertyWriter.h"

namespace lifeblood::native_clang
{
void NativePropertyWriter::Set(
    Symbol& symbol,
    const std::string& property,
    const std::string& value)
{
    symbol.properties[property] = value;
}

void NativePropertyWriter::Set(
    Edge& edge,
    const std::string& property,
    const std::string& value)
{
    edge.properties[property] = value;
}

void NativePropertyWriter::SetTrue(Symbol& symbol, const std::string& property)
{
    Set(symbol, property, "true");
}

void NativePropertyWriter::SetCount(
    Symbol& symbol,
    const std::string& property,
    unsigned value)
{
    Set(symbol, property, std::to_string(value));
}
}
