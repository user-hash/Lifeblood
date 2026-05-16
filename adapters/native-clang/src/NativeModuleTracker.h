#pragma once

#include "ClangCompileCommandReader.h"
#include "NativeGraphSink.h"

#include <map>
#include <set>
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

private:
    void AddModuleSymbol();
    void AddCommandLineMacroSymbol(const CommandLineDefine& define);
    void UpdateModuleProperties();

    std::string JoinDefines() const;

    template <typename T>
    std::string Join(const T& values) const;

    std::string moduleName_;
    std::string moduleId_;
    std::string buildProfile_;
    NativeGraphSink& graph_;
    unsigned translationUnitCount_ = 0;
    std::map<std::string, std::string> commandLineDefines_;
    std::set<std::string> commandLineUndefines_;
};
}
