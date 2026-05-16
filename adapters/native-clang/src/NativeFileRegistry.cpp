#include "NativeFileRegistry.h"

#include "ClangUtilities.h"

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
    symbol.visibility = "internal";
    const bool isHeader = EndsWith(relativePath, ".h") || EndsWith(relativePath, ".hpp");
    symbol.properties["native.kind"] = isHeader ? "header" : "translationUnit";
    symbol.properties["native.buildProfile"] = buildProfile_;
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
    UpdateTranslationUnitHealth(relativePath, "pending", {});
    graph_.UpdateSymbol("file:" + relativePath, [&](Symbol& file) {
        file.properties["native.parseArgumentCount"] =
            std::to_string(command.parseArguments.size());
        file.properties["native.commandLineDefineCount"] =
            std::to_string(command.defines.size());
        file.properties["native.commandLineUndefineCount"] =
            std::to_string(command.undefines.size());
    });
}

void NativeFileRegistry::MarkTranslationUnitParsed(
    const std::string& relativePath,
    const NativeDiagnosticSummary& diagnostics)
{
    UpdateTranslationUnitHealth(relativePath, "parsed", diagnostics);
}

void NativeFileRegistry::MarkTranslationUnitFailed(const std::string& relativePath)
{
    UpdateTranslationUnitHealth(relativePath, "failed", {});
}

void NativeFileRegistry::UpdateTranslationUnitHealth(
    const std::string& relativePath,
    const std::string& parseStatus,
    const NativeDiagnosticSummary& diagnostics)
{
    EnsureFileSymbol(relativePath);
    graph_.UpdateSymbol("file:" + relativePath, [&](Symbol& file) {
        file.properties["native.translationUnit"] = "true";
        file.properties["native.parseStatus"] = parseStatus;
        file.properties["native.warningDiagnosticCount"] = std::to_string(diagnostics.warningCount);
        file.properties["native.errorDiagnosticCount"] = std::to_string(diagnostics.errorCount);
        file.properties["native.fatalDiagnosticCount"] = std::to_string(diagnostics.fatalCount);
    });
}

void NativeFileRegistry::UpdateModuleFileProperties()
{
    graph_.UpdateSymbol(moduleId_, [&](Symbol& module) {
        module.properties["native.translationUnitFileCount"] =
            std::to_string(translationUnitFileCount_);
        module.properties["native.headerFileCount"] = std::to_string(headerFileCount_);
    });
}
}
