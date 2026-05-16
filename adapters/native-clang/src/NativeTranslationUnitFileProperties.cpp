#include "NativeTranslationUnitFileProperties.h"

#include "NativeGraphPropertyKeys.h"
#include "NativePropertyWriter.h"

namespace lifeblood::native_clang
{
void NativeTranslationUnitFileProperties::WriteCompileCommand(
    Symbol& file,
    const NativeCompileCommand& command)
{
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::ParseArgumentCount,
        static_cast<unsigned>(command.parseArguments.size()));
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::CommandLineDefineCount,
        static_cast<unsigned>(command.defines.size()));
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::CommandLineUndefineCount,
        static_cast<unsigned>(command.undefines.size()));
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::IncludeSearchPathCount,
        command.includeSearchPathCount);
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::SystemIncludeSearchPathCount,
        command.systemIncludeSearchPathCount);
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::QuoteIncludeSearchPathCount,
        command.quoteIncludeSearchPathCount);
    NativePropertyWriter::Set(
        file,
        NativeGraphPropertyKeys::SourceLanguage,
        command.sourceLanguage);
    if (!command.languageStandard.empty())
        NativePropertyWriter::Set(
            file,
            NativeGraphPropertyKeys::LanguageStandard,
            command.languageStandard);
}

void NativeTranslationUnitFileProperties::WriteParseHealth(
    Symbol& file,
    const std::string& parseStatus,
    const NativeDiagnosticSummary& diagnostics)
{
    NativePropertyWriter::SetTrue(file, NativeGraphPropertyKeys::TranslationUnit);
    NativePropertyWriter::Set(file, NativeGraphPropertyKeys::ParseStatus, parseStatus);
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::WarningDiagnosticCount,
        diagnostics.warningCount);
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::ErrorDiagnosticCount,
        diagnostics.errorCount);
    NativePropertyWriter::SetCount(
        file,
        NativeGraphPropertyKeys::FatalDiagnosticCount,
        diagnostics.fatalCount);
}
}
