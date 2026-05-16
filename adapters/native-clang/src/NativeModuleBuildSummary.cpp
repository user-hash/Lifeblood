#include "NativeModuleBuildSummary.h"

#include "NativeGraphPropertyKeys.h"
#include "NativeParseStatuses.h"
#include "NativePropertyWriter.h"

#include <sstream>
#include <vector>

namespace lifeblood::native_clang
{
namespace
{
template <typename T>
std::string JoinValues(const T& values)
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

const char* ParseStatusFor(unsigned failedTranslationUnitCount)
{
    return failedTranslationUnitCount == 0
        ? NativeParseStatuses::Complete
        : NativeParseStatuses::Partial;
}
}

void NativeModuleBuildSummary::ObserveTranslationUnit(const NativeCompileCommand& command)
{
    translationUnitCount_++;

    for (const auto& define : command.defines)
        commandLineDefines_[define.name] = define.value;

    for (const auto& name : command.undefines)
        commandLineUndefines_.insert(name);

    if (!command.sourceLanguage.empty())
        sourceLanguages_.insert(command.sourceLanguage);
    if (!command.languageStandard.empty())
        languageStandards_.insert(command.languageStandard);

    includeSearchPathCount_ += command.includeSearchPathCount;
    systemIncludeSearchPathCount_ += command.systemIncludeSearchPathCount;
    quoteIncludeSearchPathCount_ += command.quoteIncludeSearchPathCount;
}

void NativeModuleBuildSummary::ObserveParsedTranslationUnit(
    const NativeDiagnosticSummary& diagnostics)
{
    parsedTranslationUnitCount_++;
    diagnostics_.warningCount += diagnostics.warningCount;
    diagnostics_.errorCount += diagnostics.errorCount;
    diagnostics_.fatalCount += diagnostics.fatalCount;
}

void NativeModuleBuildSummary::ObserveFailedTranslationUnit()
{
    failedTranslationUnitCount_++;
}

void NativeModuleBuildSummary::WriteProperties(Symbol& module) const
{
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
        ParseStatusFor(failedTranslationUnitCount_));

    if (!commandLineDefines_.empty())
        NativePropertyWriter::Set(module, NativeGraphPropertyKeys::Defines, JoinDefines());
    if (!commandLineUndefines_.empty())
        NativePropertyWriter::Set(
            module,
            NativeGraphPropertyKeys::Undefines,
            JoinValues(commandLineUndefines_));
    if (!sourceLanguages_.empty())
        NativePropertyWriter::Set(
            module,
            NativeGraphPropertyKeys::SourceLanguages,
            JoinValues(sourceLanguages_));
    if (!languageStandards_.empty())
        NativePropertyWriter::Set(
            module,
            NativeGraphPropertyKeys::LanguageStandards,
            JoinValues(languageStandards_));

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
}

std::string NativeModuleBuildSummary::JoinDefines() const
{
    std::vector<std::string> values;
    values.reserve(commandLineDefines_.size());
    for (const auto& [name, value] : commandLineDefines_)
        values.push_back(name + "=" + value);
    return JoinValues(values);
}
}
