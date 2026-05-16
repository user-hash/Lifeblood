#pragma once

#include "GraphModel.h"
#include "NativeDirectionalSymbolCounts.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeFileCallSymbolMetrics
{
public:
    void RecordSameFileCall(const std::string& sourceId, const std::string& targetId);
    void RecordCrossFileCall(const std::string& sourceId, const std::string& targetId);
    void Decorate(Symbol& symbol) const;

private:
    NativeDirectionalSymbolCounts sameFileCalls_;
    NativeDirectionalSymbolCounts crossFileCalls_;
};
}
