namespace Lifeblood.Domain.Results;

/// <summary>
/// Pure result types for compilation operations.
/// Language-agnostic — any adapter can produce these.
/// Zero dependencies (Domain layer).
/// </summary>

public sealed class DiagnosticInfo
{
    public required string Id { get; init; }
    public required string Message { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
    public string? Module { get; init; }
}

public enum DiagnosticSeverity
{
    Hidden,
    Info,
    Warning,
    Error,
}

public sealed class CompileCheckResult
{
    public required bool Success { get; init; }
    public DiagnosticInfo[] Diagnostics { get; init; } = Array.Empty<DiagnosticInfo>();
}

public sealed class CodeExecutionResult
{
    public required bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public string? ReturnValue { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed class ReferenceLocation
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public string ContainingSymbolId { get; init; } = "";
    public string SpanText { get; init; } = "";
}

public sealed class TextEdit
{
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int StartColumn { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string NewText { get; init; }
}

/// <summary>
/// Where a symbol is defined (declaration site).
/// </summary>
public sealed class DefinitionLocation
{
    public required string SymbolId { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public string DisplayName { get; init; } = "";
    public string Documentation { get; init; } = "";
}

/// <summary>
/// A symbol resolved from a source position (line/column).
/// </summary>
public sealed class SymbolAtPosition
{
    public required string SymbolId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string QualifiedName { get; init; } = "";
    public string Documentation { get; init; } = "";
}
