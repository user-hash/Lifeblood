#include "NativeModuleTracker.h"

#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"
#include "NativeMacroSources.h"
#include "NativeParseStatuses.h"
#include "NativePropertyWriter.h"
#include "NativeSymbolIds.h"
#include "NativeVisibilityNames.h"

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
    NativePropertyWriter::Set(module, NativeGraphPropertyKeys::NativeKind, NativeKindNames::Library);
    NativePropertyWriter::Set(module, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
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
    symbol.visibility = NativeVisibilityNames::Internal;
    symbol.isStatic = true;
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::NativeKind, NativeKindNames::Macro);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::MacroSource,
        NativeMacroSources::CommandLine);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::MacroValue, define.value);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(symbol);
}

void NativeModuleTracker::UpdateModuleProperties()
{
    graph_.UpdateSymbol(moduleId_, [this](Symbol& module) {
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::TranslationUnitCount,
            translationUnitCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::ParsedTranslationUnitCount,
            parsedTranslationUnitCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::FailedTranslationUnitCount,
            failedTranslationUnitCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::WarningDiagnosticCount,
            diagnostics_.warningCount);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::ErrorDiagnosticCount,
            diagnostics_.errorCount);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::FatalDiagnosticCount,
            diagnostics_.fatalCount);
        NativePropertyWriter::Set(
            module,
            NativeGraphPropertyKeys::ParseStatus,
            failedTranslationUnitCount_ == 0
                ? NativeParseStatuses::Complete
                : NativeParseStatuses::Partial);
        if (!commandLineDefines_.empty())
            NativePropertyWriter::Set(module, NativeGraphPropertyKeys::Defines, JoinDefines());
        if (!commandLineUndefines_.empty())
            NativePropertyWriter::Set(
                module,
                NativeGraphPropertyKeys::Undefines,
                Join(commandLineUndefines_));
        if (!sourceLanguages_.empty())
            NativePropertyWriter::Set(
                module,
                NativeGraphPropertyKeys::SourceLanguages,
                Join(sourceLanguages_));
        if (!languageStandards_.empty())
            NativePropertyWriter::Set(
                module,
                NativeGraphPropertyKeys::LanguageStandards,
                Join(languageStandards_));
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::IncludeSearchPathCount,
            includeSearchPathCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::SystemIncludeSearchPathCount,
            systemIncludeSearchPathCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::QuoteIncludeSearchPathCount,
            quoteIncludeSearchPathCount_);
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
