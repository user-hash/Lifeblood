#include "ClangTranslationUnitParser.h"

#include "ClangUtilities.h"

#include <iostream>
#include <utility>
#include <vector>

namespace lifeblood::native_clang
{
ParsedTranslationUnit::ParsedTranslationUnit(CXTranslationUnit unit)
    : unit_(unit)
{
}

ParsedTranslationUnit::~ParsedTranslationUnit()
{
    if (unit_ != nullptr)
        clang_disposeTranslationUnit(unit_);
}

ParsedTranslationUnit::ParsedTranslationUnit(ParsedTranslationUnit&& other) noexcept
    : unit_(std::exchange(other.unit_, nullptr))
{
}

ParsedTranslationUnit& ParsedTranslationUnit::operator=(ParsedTranslationUnit&& other) noexcept
{
    if (this == &other) return *this;

    if (unit_ != nullptr)
        clang_disposeTranslationUnit(unit_);
    unit_ = std::exchange(other.unit_, nullptr);
    return *this;
}

ParsedTranslationUnit ClangTranslationUnitParser::Parse(
    CXIndex index,
    const NativeCompileCommand& command) const
{
    std::vector<const char*> cArgs;
    cArgs.reserve(command.parseArguments.size());
    for (const auto& arg : command.parseArguments)
        cArgs.push_back(arg.c_str());

    CXTranslationUnit unit = nullptr;
    const unsigned parseOptions = CXTranslationUnit_DetailedPreprocessingRecord;
    CXErrorCode parseResult = clang_parseTranslationUnit2(
        index,
        command.sourcePath.string().c_str(),
        cArgs.data(),
        static_cast<int>(cArgs.size()),
        nullptr,
        0,
        parseOptions,
        &unit);

    if (parseResult != CXError_Success || unit == nullptr)
    {
        std::cerr << "Failed to parse " << command.sourcePath.string()
                  << " (CXErrorCode " << parseResult << ")\n";
        return ParsedTranslationUnit{};
    }

    ReportDiagnostics(unit);
    return ParsedTranslationUnit(unit);
}

void ClangTranslationUnitParser::ReportDiagnostics(CXTranslationUnit unit) const
{
    const unsigned diagnosticCount = clang_getNumDiagnostics(unit);
    for (unsigned i = 0; i < diagnosticCount; i++)
    {
        CXDiagnostic diagnostic = clang_getDiagnostic(unit, i);
        auto severity = clang_getDiagnosticSeverity(diagnostic);
        if (severity >= CXDiagnostic_Error)
        {
            std::cerr << ToString(clang_formatDiagnostic(
                diagnostic,
                clang_defaultDiagnosticDisplayOptions())) << "\n";
        }
        clang_disposeDiagnostic(diagnostic);
    }
}
}
