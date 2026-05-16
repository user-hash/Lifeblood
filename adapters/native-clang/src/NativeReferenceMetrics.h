#pragma once

#include "GraphModel.h"
#include "NativeDirectionalSymbolCounts.h"
#include "NativeEdgeClassification.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeReferenceMetrics
{
public:
    void Clear();
    void RecordAcceptedReference(const Edge& edge);
    void DecorateSymbol(Symbol& symbol) const;

private:
    void RecordReferenceCounts(const std::string& sourceId, const std::string& targetId);
    void RecordCallbackTargetCounts(const std::string& sourceId, const std::string& targetId);
    void RecordGlobalAccessCounts(const std::string& sourceId, const std::string& targetId);
    void RecordFieldAccessCounts(const std::string& sourceId, const std::string& targetId);
    void RecordParameterTypeCounts(const std::string& sourceId, const std::string& targetId);

    NativeDirectionalSymbolCounts referenceCounts_;
    NativeDirectionalSymbolCounts callbackTargetCounts_;
    NativeDirectionalSymbolCounts globalAccessCounts_;
    NativeDirectionalSymbolCounts fieldAccessCounts_;
    NativeDirectionalSymbolCounts parameterTypeCounts_;
};
}
