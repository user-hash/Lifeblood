#pragma once

#include "NativeDiagnosticSummary.h"
#include "NativeCompileCommand.h"

#include <clang-c/Index.h>

namespace lifeblood::native_clang
{
class ParsedTranslationUnit
{
public:
    explicit ParsedTranslationUnit(
        CXTranslationUnit unit = nullptr,
        NativeDiagnosticSummary diagnostics = {});
    ~ParsedTranslationUnit();

    ParsedTranslationUnit(const ParsedTranslationUnit&) = delete;
    ParsedTranslationUnit& operator=(const ParsedTranslationUnit&) = delete;

    ParsedTranslationUnit(ParsedTranslationUnit&& other) noexcept;
    ParsedTranslationUnit& operator=(ParsedTranslationUnit&& other) noexcept;

    CXTranslationUnit Get() const { return unit_; }
    const NativeDiagnosticSummary& Diagnostics() const { return diagnostics_; }
    explicit operator bool() const { return unit_ != nullptr; }

private:
    CXTranslationUnit unit_ = nullptr;
    NativeDiagnosticSummary diagnostics_;
};

class ClangTranslationUnitParser
{
public:
    ParsedTranslationUnit Parse(CXIndex index, const NativeCompileCommand& command) const;

private:
    NativeDiagnosticSummary ReportDiagnostics(CXTranslationUnit unit) const;
};
}
