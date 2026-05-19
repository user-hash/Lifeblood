#include "NativeFunctionEmitter.h"

#include "NativeDeclarationKinds.h"
#include "NativeFileRegistry.h"
#include "NativeGraphPropertyKeys.h"
#include "NativeGraphSink.h"
#include "NativeKindNames.h"
#include "NativeLinkageNames.h"
#include "NativePropertyWriter.h"
#include "NativeVisibilityNames.h"

#include <utility>

namespace lifeblood::native_clang
{
NativeFunctionEmitter::NativeFunctionEmitter(
    std::string buildProfile,
    NativeGraphSink& graph,
    NativeFileRegistry& files)
    : buildProfile_(std::move(buildProfile)),
      graph_(graph),
      files_(files)
{
}

NativeFunctionEmissionStatus NativeFunctionEmitter::AddFunction(
    const NativeFunctionDeclarationFacts& facts)
{
    if (facts.filePath.empty() || facts.symbolId.empty() || facts.name.empty())
        return NativeFunctionEmissionStatus::Rejected;

    files_.EnsureFileSymbol(facts.filePath);

    Symbol symbol;
    symbol.id = facts.symbolId;
    if (ExistingDefinitionShouldWin(symbol.id, facts.isDefinition))
        return NativeFunctionEmissionStatus::ExistingDefinitionRetained;

    symbol.name = facts.name;
    symbol.qualifiedName = facts.name;
    symbol.kind = "method";
    symbol.filePath = facts.filePath;
    symbol.line = facts.line;
    symbol.parentId = "file:" + facts.filePath;
    symbol.visibility = facts.isStatic
        ? NativeVisibilityNames::Private
        : NativeVisibilityNames::Public;
    symbol.isStatic = facts.isStatic;
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::NativeKind,
        NativeKindNames::Function);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::DeclarationKind,
        facts.isDefinition ? NativeDeclarationKinds::Definition : NativeDeclarationKinds::Declaration);
    NativePropertyWriter::Set(
        symbol,
        NativeGraphPropertyKeys::Linkage,
        facts.isStatic ? NativeLinkageNames::Internal : NativeLinkageNames::External);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::Signature, facts.signature);
    NativePropertyWriter::Set(symbol, NativeGraphPropertyKeys::BuildProfile, buildProfile_);
    graph_.AddSymbol(symbol);

    return NativeFunctionEmissionStatus::Emitted;
}

bool NativeFunctionEmitter::ExistingDefinitionShouldWin(
    const std::string& symbolId,
    bool isDefinition) const
{
    if (isDefinition) return false;

    const Symbol* existing = graph_.FindSymbol(symbolId);
    if (existing == nullptr) return false;

    auto it = existing->properties.find(NativeGraphPropertyKeys::DeclarationKind);
    return it != existing->properties.end() && it->second == NativeDeclarationKinds::Definition;
}
}
