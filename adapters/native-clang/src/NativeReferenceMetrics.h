#pragma once

#include "GraphModel.h"
#include "NativeEdgeClassification.h"

#include <map>
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

    std::map<std::string, unsigned> referenceOutCounts_;
    std::map<std::string, unsigned> referenceInCounts_;
    std::map<std::string, unsigned> callbackTargetOutCounts_;
    std::map<std::string, unsigned> callbackTargetInCounts_;
    std::map<std::string, unsigned> globalAccessOutCounts_;
    std::map<std::string, unsigned> globalAccessInCounts_;
    std::map<std::string, unsigned> fieldAccessOutCounts_;
    std::map<std::string, unsigned> fieldAccessInCounts_;
};
}
