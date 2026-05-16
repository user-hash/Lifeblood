#pragma once

#include "GraphModel.h"
#include "NativeCompileCommand.h"
#include "NativeDiagnosticSummary.h"

#include <string>

namespace lifeblood::native_clang
{
class NativeTranslationUnitFileProperties
{
public:
    static void WriteCompileCommand(Symbol& file, const NativeCompileCommand& command);
    static void WriteParseHealth(
        Symbol& file,
        const std::string& parseStatus,
        const NativeDiagnosticSummary& diagnostics);
};
}
