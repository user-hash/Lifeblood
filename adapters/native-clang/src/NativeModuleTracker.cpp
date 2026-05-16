#include "NativeModuleTracker.h"

#include "NativeSymbolIds.h"

#include <sstream>
#include <utility>
#include <vector>

namespace lifeblood::native_clang
{
NativeModuleTracker::NativeModuleTracker(
    std::string moduleName,
    std::string buildProfile,
    NativeGraphSink& graph)
    : moduleName_(std::move(moduleName)),
      moduleId_("mod:" + moduleName_),
      buildProfile_(std::move(buildProfile)),
      graph_(graph)
{
    AddModuleSymbol();
}

void NativeModuleTracker::BeginTranslationUnit(const NativeCompileCommand& command)
{
    translationUnitCount_++;

    for (const auto& define : command.defines)
    {
        commandLineDefines_[define.name] = define.value;
        AddCommandLineMacroSymbol(define);
    }

    for (const auto& name : command.undefines)
        commandLineUndefines_.insert(name);

    if (!command.sourceLanguage.empty())
        sourceLanguages_.insert(command.sourceLanguage);
    if (!command.languageStandard.empty())
        languageStandards_.insert(command.languageStandard);
    includeSearchPathCount_ += command.includeSearchPathCount;
    systemIncludeSearchPathCount_ += command.systemIncludeSearchPathCount;
    quoteIncludeSearchPathCount_ += command.quoteIncludeSearchPathCount;

    UpdateModuleProperties();
}

void NativeModuleTracker::RecordTranslationUnitParsed(const NativeDiagnosticSummary& diagnostics)
{
    parsedTranslationUnitCount_++;
    diagnostics_.warningCount += diagnostics.warningCount;
    diagnostics_.errorCount += diagnostics.errorCount;
    diagnostics_.fatalCount += diagnostics.fatalCount;
    UpdateModuleProperties();
}

void NativeModuleTracker::RecordTranslationUnitFailed()
{
    failedTranslationUnitCount_++;
    UpdateModuleProperties();
}

void NativeModuleTracker::AddModuleSymbol()
{
    Symbol module;
    module.id = moduleId_;
    module.name = moduleName_;
    module.qualifiedName = moduleName_;
    module.kind = "module";
    module.properties["native.kind"] = "library";
    module.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(module);
}

void NativeModuleTracker::AddCommandLineMacroSymbol(const CommandLineDefine& define)
{
    Symbol symbol;
    symbol.id = MacroId(define.name);
    symbol.name = define.name;
    symbol.qualifiedName = define.name;
    symbol.kind = "field";
    symbol.parentId = moduleId_;
    symbol.visibility = "internal";
    symbol.isStatic = true;
    symbol.properties["native.kind"] = "macro";
    symbol.properties["native.macroSource"] = "commandLine";
    symbol.properties["native.macroValue"] = define.value;
    symbol.properties["native.buildProfile"] = buildProfile_;
    graph_.AddSymbol(symbol);
}

void NativeModuleTracker::UpdateModuleProperties()
{
    graph_.UpdateSymbol(moduleId_, [this](Symbol& module) {
        module.properties["native.translationUnitCount"] = std::to_string(translationUnitCount_);
        module.properties["native.parsedTranslationUnitCount"] =
            std::to_string(parsedTranslationUnitCount_);
        module.properties["native.failedTranslationUnitCount"] =
            std::to_string(failedTranslationUnitCount_);
        module.properties["native.warningDiagnosticCount"] =
            std::to_string(diagnostics_.warningCount);
        module.properties["native.errorDiagnosticCount"] =
            std::to_string(diagnostics_.errorCount);
        module.properties["native.fatalDiagnosticCount"] =
            std::to_string(diagnostics_.fatalCount);
        module.properties["native.parseStatus"] =
            failedTranslationUnitCount_ == 0 ? "complete" : "partial";
        if (!commandLineDefines_.empty())
            module.properties["native.defines"] = JoinDefines();
        if (!commandLineUndefines_.empty())
            module.properties["native.undefines"] = Join(commandLineUndefines_);
        if (!sourceLanguages_.empty())
            module.properties["native.sourceLanguages"] = Join(sourceLanguages_);
        if (!languageStandards_.empty())
            module.properties["native.languageStandards"] = Join(languageStandards_);
        module.properties["native.includeSearchPathCount"] =
            std::to_string(includeSearchPathCount_);
        module.properties["native.systemIncludeSearchPathCount"] =
            std::to_string(systemIncludeSearchPathCount_);
        module.properties["native.quoteIncludeSearchPathCount"] =
            std::to_string(quoteIncludeSearchPathCount_);
    });
}

std::string NativeModuleTracker::JoinDefines() const
{
    std::vector<std::string> values;
    values.reserve(commandLineDefines_.size());
    for (const auto& [name, value] : commandLineDefines_)
        values.push_back(name + "=" + value);
    return Join(values);
}

template <typename T>
std::string NativeModuleTracker::Join(const T& values) const
{
    std::ostringstream output;
    bool first = true;
    for (const auto& value : values)
    {
        if (!first) output << ";";
        first = false;
        output << value;
    }
    return output.str();
}
}
