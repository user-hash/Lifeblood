#include "NativeModuleTracker.h"

#include "NativeGraphPropertyKeys.h"
#include "NativeKindNames.h"
#include "NativeMacroSources.h"
#include "NativePropertyWriter.h"
#include "NativeSymbolIds.h"
#include "NativeVisibilityNames.h"

#include <utility>

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
    buildSummary_.ObserveTranslationUnit(command);

    for (const auto& define : command.defines)
        AddCommandLineMacroSymbol(define);

    UpdateModuleProperties();
}

void NativeModuleTracker::RecordTranslationUnitParsed(const NativeDiagnosticSummary& diagnostics)
{
    buildSummary_.ObserveParsedTranslationUnit(diagnostics);
    UpdateModuleProperties();
}

void NativeModuleTracker::RecordTranslationUnitFailed()
{
    buildSummary_.ObserveFailedTranslationUnit();
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
        buildSummary_.WriteProperties(module);
    });
}
}
