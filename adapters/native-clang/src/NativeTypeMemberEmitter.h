#pragma once

#include "NativeTypeMemberDeclarationFacts.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeGraphSink;

class NativeTypeMemberEmitter
{
public:
    NativeTypeMemberEmitter(
        std::string buildProfile,
        NativeGraphSink& graph);

    bool AddEnumConstant(const NativeEnumConstantDeclarationFacts& facts);
    bool AddField(const NativeFieldDeclarationFacts& facts);

private:
    std::string buildProfile_;
    NativeGraphSink& graph_;
};
}
