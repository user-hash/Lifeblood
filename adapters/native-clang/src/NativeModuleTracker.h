#pragma once

#include "NativeCompileCommand.h"
#include "NativeDiagnosticSummary.h"
#include "NativeGraphSink.h"
#include "NativeModuleBuildSummary.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeModuleTracker
{
public:
    NativeModuleTracker(
        std::string moduleName,
        std::string buildProfile,
        NativeGraphSink& graph);

    const std::string& ModuleName() const { return moduleName_; }
    const std::string& ModuleId() const { return moduleId_; }

    void BeginTranslationUnit(const NativeCompileCommand& command);
    void RecordTranslationUnitParsed(const NativeDiagnosticSummary& diagnostics);
    void RecordTranslationUnitFailed();

private:
    void AddModuleSymbol();
    void AddCommandLineMacroSymbol(const CommandLineDefine& define);
    void UpdateModuleProperties();

    std::string moduleName_;
    std::string moduleId_;
    std::string buildProfile_;
    NativeGraphSink& graph_;
    NativeModuleBuildSummary buildSummary_;
};
}
