#pragma once

#include "GraphModel.h"

#include <map>
#include <string>

namespace lifeblood::native_clang
{
class NativeReferenceMetrics
{
public:
    void Clear();
    void RecordAcceptedReference(const std::string& sourceId, const std::string& targetId);
    void DecorateSymbol(Symbol& symbol) const;

private:
    std::map<std::string, unsigned> referenceOutCounts_;
    std::map<std::string, unsigned> referenceInCounts_;
};
}
