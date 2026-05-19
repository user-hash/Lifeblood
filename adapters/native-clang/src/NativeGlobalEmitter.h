#pragma once

#include "NativeGlobalDeclarationFacts.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeFileRegistry;
class NativeGraphSink;

class NativeGlobalEmitter
{
public:
    NativeGlobalEmitter(
        std::string buildProfile,
        NativeGraphSink& graph,
        NativeFileRegistry& files);

    bool AddGlobalVariable(const NativeGlobalDeclarationFacts& facts);

private:
    std::string buildProfile_;
    NativeGraphSink& graph_;
    NativeFileRegistry& files_;
};
}
