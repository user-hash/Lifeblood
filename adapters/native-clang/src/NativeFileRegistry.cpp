#include "NativeFileRegistry.h"

#include "ClangUtilities.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"
#include "NativeParseStatuses.h"
#include "NativePropertyWriter.h"
#include "NativeVisibilityNames.h"

#include <filesystem>
#include <utility>

namespace fs = std::filesystem;

namespace lifeblood::native_clang
{
NativeFileRegistry::NativeFileRegistry(
    std::string moduleName,
    std::string moduleId,
    std::string buildProfile,
    NativeGraphSink& graph)
    : moduleName_(std::move(moduleName)),
      moduleId_(std::move(moduleId)),
      buildProfile_(std::move(buildProfile)),
      graph_(graph)
{
}

void NativeFileRegistry::EnsureFileSymbol(const std::string& relativePath)
{
    std::string id = "file:" + relativePath;
    if (graph_.HasSymbol(id)) return;

    Symbol symbol;
    symbol.id = id;
    symbol.name = fs::path(relativePath).filename().string();
    symbol.qualifiedName = moduleName_ + "/" + relativePath;
    symbol.kind = "file";
    symbol.filePath = relativePath;
    symbol.parentId = moduleId_;
    symbol.visibility = NativeVisibilityNames::Internal;
    const bool isHeader = EndsWith(relativePath, ".h") || EndsWith(relativePath, ".hpp");
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        isHeader ? NativeKindNames::Header : NativeKindNames::TranslationUnit);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(symbol);

    if (isHeader)
        headerFileCount_++;
    else
        translationUnitFileCount_++;
    UpdateModuleFileProperties();
}

void NativeFileRegistry::MarkTranslationUnitPending(
    const std::string& relativePath,
    const NativeCompileCommand& command)
{
    UpdateTranslationUnitHealth(relativePath, NativeParseStatuses::Pending, {});
    graph_.UpdateSymbol("file:" + relativePath, [&](Symbol& file) {
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
    });
}

void NativeFileRegistry::MarkTranslationUnitParsed(
    const std::string& relativePath,
    const NativeDiagnosticSummary& diagnostics)
{
    UpdateTranslationUnitHealth(relativePath, NativeParseStatuses::Parsed, diagnostics);
}

void NativeFileRegistry::MarkTranslationUnitFailed(const std::string& relativePath)
{
    UpdateTranslationUnitHealth(relativePath, NativeParseStatuses::Failed, {});
}

void NativeFileRegistry::UpdateTranslationUnitHealth(
    const std::string& relativePath,
    const std::string& parseStatus,
    const NativeDiagnosticSummary& diagnostics)
{
    EnsureFileSymbol(relativePath);
    graph_.UpdateSymbol("file:" + relativePath, [&](Symbol& file) {
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
    });
}

void NativeFileRegistry::UpdateModuleFileProperties()
{
    graph_.UpdateSymbol(moduleId_, [&](Symbol& module) {
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::TranslationUnitFileCount,
            translationUnitFileCount_);
        NativePropertyWriter::SetCount(
            module,
            NativeGraphPropertyKeys::HeaderFileCount,
            headerFileCount_);
    });
}
}
