#pragma once

#include "NativeCompileCommand.h"

#include <clang-c/CXCompilationDatabase.h>

#include <string>

namespace lifeblood::native_clang
{
class ClangCommandLineMacroCollector
{
public:
    void Collect(CXCompileCommand command, NativeCompileCommand& result) const;

private:
    void AddDefine(const std::string& raw, NativeCompileCommand& result) const;
    void AddUndefine(const std::string& name, NativeCompileCommand& result) const;
};
}
