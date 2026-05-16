#pragma once

#include "GraphModel.h"
#include "NativeCompileCommand.h"
#include "NativeDiagnosticSummary.h"

#include <map>
#include <set>
#include <string>

namespace lifeblood::native_clang
{
class NativeModuleBuildSummary
{
public:
    void ObserveTranslationUnit(const NativeCompileCommand& command);
    void ObserveParsedTranslationUnit(const NativeDiagnosticSummary& diagnostics);
    void ObserveFailedTranslationUnit();

    void WriteProperties(Symbol& module) const;

private:
    std::string JoinDefines() const;

    unsigned translationUnitCount_ = 0;
    unsigned parsedTranslationUnitCount_ = 0;
    unsigned failedTranslationUnitCount_ = 0;
    NativeDiagnosticSummary diagnostics_;
    std::map<std::string, std::string> commandLineDefines_;
    std::set<std::string> commandLineUndefines_;
    std::set<std::string> sourceLanguages_;
    std::set<std::string> languageStandards_;
    unsigned includeSearchPathCount_ = 0;
    unsigned systemIncludeSearchPathCount_ = 0;
    unsigned quoteIncludeSearchPathCount_ = 0;
};
}
