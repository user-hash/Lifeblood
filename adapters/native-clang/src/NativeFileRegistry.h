#pragma once

#include "NativeDiagnosticSummary.h"
#include "NativeGraphSink.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeFileRegistry
{
public:
    NativeFileRegistry(
        std::string moduleName,
        std::string moduleId,
        std::string buildProfile,
        NativeGraphSink& graph);

    void EnsureFileSymbol(const std::string& relativePath);
    void MarkTranslationUnitPending(const std::string& relativePath);
    void MarkTranslationUnitParsed(
        const std::string& relativePath,
        const NativeDiagnosticSummary& diagnostics);
    void MarkTranslationUnitFailed(const std::string& relativePath);

private:
    void UpdateTranslationUnitHealth(
        const std::string& relativePath,
        const std::string& parseStatus,
        const NativeDiagnosticSummary& diagnostics);

    std::string moduleName_;
    std::string moduleId_;
    std::string buildProfile_;
    NativeGraphSink& graph_;
};
}
