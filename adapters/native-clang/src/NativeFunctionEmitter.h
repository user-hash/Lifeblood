#pragma once

#include "NativeFunctionDeclarationFacts.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeFileRegistry;
class NativeGraphSink;

enum class NativeFunctionEmissionStatus
{
    Rejected,
    Emitted,
    ExistingDefinitionRetained
};

class NativeFunctionEmitter
{
public:
    NativeFunctionEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        NativeFileRegistry& files);

    NativeFunctionEmissionStatus AddFunction(const NativeFunctionDeclarationFacts& facts);

private:
    bool ExistingDefinitionShouldWin(const std::string& symbolId, bool isDefinition) const;

    std::string buildProfile_;
    NativeGraphSink& graph_;
    NativeFileRegistry& files_;
};
}
