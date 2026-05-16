#pragma once

namespace lifeblood::native_clang
{
struct NativeDiagnosticSummary
{
    unsigned warningCount = 0;
    unsigned errorCount = 0;
    unsigned fatalCount = 0;
};
}
