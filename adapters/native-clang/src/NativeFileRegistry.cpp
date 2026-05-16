#include "NativeFileRegistry.h"

#include "ClangUtilities.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"
#include "NativeParseStatuses.h"
#include "NativePropertyWriter.h"
#include "NativeTranslationUnitFileProperties.h"
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
        NativeTranslationUnitFileProperties::WriteCompileCommand(file, command);
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
        NativeTranslationUnitFileProperties::WriteParseHealth(
            file,
            parseStatus,
            diagnostics);
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
