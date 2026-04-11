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
    /// <summary>
    /// Whether this location is a declaration site of the symbol (e.g. the
    /// partial-type declaration header or the method definition) or a
    /// usage site (an invocation, an identifier reference, a member access).
    /// Added 2026-04-11 to close DAWG F3. Defaults to <see cref="ReferenceKind.Usage"/>
    /// so existing emitters keep the old meaning until they opt into tagging.
    /// </summary>
    public ReferenceKind Kind { get; init; } = ReferenceKind.Usage;
}

/// <summary>
/// Structural classification of a <see cref="ReferenceLocation"/>. Used so
/// consumers (e.g. the find_references tool) can filter or group locations
/// by whether they declare the symbol or reference it. Language-agnostic.
/// </summary>
public enum ReferenceKind
{
    /// <summary>The location references the symbol (invocation, identifier, member access, type-use).</summary>
    Usage,
    /// <summary>The location declares the symbol (type header, method signature, partial declaration).</summary>
    Declaration,
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

/// <summary>
/// A file the analyzer declined to process, with a machine-readable reason
/// for why. Added 2026-04-11 (Phase 4 / C4) to close DAWG B4 — previously
/// the analyzer silently dropped files (missing on disk, non-.cs extension,
/// outside any tracked module) and users had no way to discover that their
/// change wasn't included in the graph.
///
/// Language-agnostic: each adapter reports its own reasons using the
/// canonical string codes in <see cref="SkipReason"/>.
/// </summary>
public sealed class SkippedFile
{
    public required string FilePath { get; init; }
    public required string Reason { get; init; }
    public string ModuleName { get; init; } = "";
}

/// <summary>
/// Canonical reason codes for <see cref="SkippedFile.Reason"/>. These are
/// string constants so new reasons can be added without bumping an enum
/// and breaking adapter contracts.
/// </summary>
public static class SkipReason
{
    /// <summary>File listed in a csproj but not present on disk.</summary>
    public const string FileNotFound = "file-not-found";
    /// <summary>File has an extension the adapter does not parse (non-.cs in the C# adapter, etc.).</summary>
    public const string UnsupportedExtension = "unsupported-extension";
    /// <summary>File lives outside any tracked module.</summary>
    public const string OutsideTrackedModule = "outside-tracked-module";
}
