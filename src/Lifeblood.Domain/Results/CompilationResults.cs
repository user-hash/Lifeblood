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

    /// <summary>
    /// Module that owned the file or hosted the snippet for this check.
    /// Populated when a file-mode check found an owning module; empty
    /// when the host fell through to the first available compilation
    /// (the legacy snippet path) or the request couldn't be resolved.
    /// </summary>
    public string ResolvedModule { get; init; } = "";

    /// <summary>
    /// File-mode marker: when the file was already part of an owning
    /// compilation, the host swapped its existing syntax tree for the
    /// on-disk content (so the user gets edit-then-check semantics
    /// without colliding type re-declarations). False when the host
    /// added the input as a new tree (snippet mode, or a brand-new
    /// file not yet in any module).
    /// </summary>
    public bool ExistingTreeReplaced { get; init; }

    /// <summary>
    /// Active preprocessor symbols Lifeblood used when binding the
    /// snippet or file. Lets a caller tell apart Editor-only noise from
    /// release-build risk without re-running the tool under a different
    /// define set (e.g. distinguishing a finding gated behind
    /// <c>UNITY_EDITOR</c> from one that will fail an IL2CPP player
    /// build). Sorted ASCII-ordinal, deduplicated. Empty when the host
    /// is unavailable. INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 /
    /// LB-INBOX-008.
    /// </summary>
    public string[] DefinesActive { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Diagnostic-set result with the preprocessor scope it was bound under.
/// Returned by <c>ICompilationHost.GetDiagnosticsReport</c>; the legacy
/// <c>GetDiagnostics</c> overloads keep their array shape for callers
/// that do not care about define context. Lets a caller decide whether
/// a flagged finding is Editor-only noise or release-build risk without
/// re-running the tool under a different define set.
/// INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-INBOX-008.
/// </summary>
public sealed class DiagnosticsReport
{
    public required DiagnosticInfo[] Diagnostics { get; init; }

    /// <summary>
    /// Active preprocessor symbols for the requested scope. For
    /// file-scope or module-scope requests this is the owning
    /// compilation's parse-options symbol set. For project-wide
    /// requests it is the union across every loaded compilation
    /// (sorted ASCII-ordinal, deduplicated) — the honest answer is
    /// "diagnostics were rendered across modules with these defines
    /// active across the workspace", and the caller can re-query with
    /// <see cref="DiagnosticsRequest.ModuleName"/> for per-module
    /// precision when needed.
    /// </summary>
    public required string[] DefinesActive { get; init; }

    /// <summary>
    /// Module the report scope resolved to. Populated when the request
    /// pinned a <see cref="DiagnosticsRequest.ModuleName"/> or
    /// <see cref="DiagnosticsRequest.FilePath"/> mapped to exactly one
    /// owning compilation; empty for project-wide scope spanning every
    /// compilation.
    /// </summary>
    public string ResolvedModule { get; init; } = "";
}

/// <summary>
/// Typed request for <c>ICompilationHost.CompileCheck</c>. Pass
/// <see cref="Code"/> for inline snippets or <see cref="FilePath"/>
/// for full-file checks against the file's owning module compilation.
/// At most one of <see cref="Code"/> / <see cref="FilePath"/> is set
/// by the handler; supplying both is a caller error caught upstream.
/// </summary>
public sealed class CompileCheckRequest
{
    /// <summary>Inline source to check. Mutually exclusive with <see cref="FilePath"/>.</summary>
    public string? Code { get; init; }

    /// <summary>
    /// Workspace-relative or absolute path to a source file. The host
    /// detects which module's compilation owns the file by matching the
    /// path against each compilation's syntax tree paths, then swaps the
    /// file's existing tree for the on-disk content. Mutually exclusive
    /// with <see cref="Code"/>.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>Explicit module override. When unset, file-mode auto-detects from the path; snippet-mode falls through to the first compilation.</summary>
    public string? ModuleName { get; init; }
}

public sealed class CodeExecutionResult
{
    public required bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public string? ReturnValue { get; init; }
    public double ElapsedMs { get; init; }

    /// <summary>
    /// Non-fatal diagnostics from the runtime-assembly resolver (Phase P4).
    /// Examples: "Unity workspace detected but no build artifacts found —
    /// run a Unity build first" when the executor expected to inject
    /// UnityEngine.dll references and couldn't. Empty in the happy path.
    /// </summary>
    public string[] RuntimeAssemblyWarnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Non-fatal diagnostics from the target-runtime pre-flight (Phase P4).
    /// Each entry is a single API the script touches that isn't available
    /// in the requested target profile (e.g. <c>MathF.Log2</c> doesn't
    /// exist in <c>net-standard-2.1</c>). Empty when the script's API
    /// surface fits cleanly in the target profile, or when no profile
    /// was requested.
    /// </summary>
    public string[] TargetRuntimeWarnings { get; init; } = Array.Empty<string>();
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
    /// Defaults to <see cref="ReferenceKind.Usage"/> so existing emitters
    /// keep the old meaning until they opt into tagging.
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
/// for why. Without this list the analyzer would silently drop files
/// (missing on disk, non-.cs extension, outside any tracked module) and
/// users would have no way to discover that their change wasn't included
/// in the graph.
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
/// Per-member reference coverage for one enum type. Surfaces the
/// reference-kind distribution that lets a caller answer "is this value
/// declared but never produced?" in one call instead of pairing
/// <c>find_references</c> with manual syntax inspection per hit.
/// INV-ENUM-COVERAGE-001 / LB-TRACK-20260514-003.
/// </summary>
public sealed class EnumCoverageReport
{
    /// <summary>Canonical id of the enum type the report covers.</summary>
    public required string EnumTypeId { get; init; }

    /// <summary>Short type name (e.g. <c>FieldMask</c>) for display.</summary>
    public required string EnumTypeName { get; init; }

    /// <summary>One coverage row per declared member, in source order.</summary>
    public required EnumMemberCoverage[] Members { get; init; }

    /// <summary>
    /// Count of members where <see cref="EnumMemberCoverage.IsUnproduced"/>
    /// is true. Caller-friendly summary so the dogfood case ("how many
    /// values in this state-machine enum are never produced?") reads
    /// off the top of the response.
    /// </summary>
    public required int UnproducedCount { get; init; }

    /// <summary>
    /// Count of members with zero references of any kind. Strictly
    /// stronger than unproduced — an unreferenced value is unproduced
    /// AND unconsumed.
    /// </summary>
    public required int UnreferencedCount { get; init; }
}

/// <summary>
/// Reference-kind distribution for one enum member. All counts are
/// per-reference-site, so two production sites for the same value yield
/// <c>ProducedCount == 2</c>.
/// </summary>
public sealed class EnumMemberCoverage
{
    /// <summary>Canonical id of the enum member (field-kind, fieldKind="enumMember").</summary>
    public required string MemberId { get; init; }

    /// <summary>Short member name (e.g. <c>ShimmerPhase</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Total source-resolved references to this member (sum of all kinds + Other).</summary>
    public required int TotalReferences { get; init; }

    /// <summary>
    /// References where the member appears in a value-producing
    /// position: RHS of assignment, return-statement operand, argument
    /// passed to a method/ctor, variable initializer, yield/arrow body.
    /// </summary>
    public required int ProducedCount { get; init; }

    /// <summary>
    /// References where the member is the right-hand side of an
    /// equality / inequality comparison (<c>x == FieldMask.A</c>,
    /// <c>FieldMask.A != y</c>) or a relational comparison.
    /// </summary>
    public required int ConsumedComparisonCount { get; init; }

    /// <summary>
    /// References where the member appears in a switch / pattern arm
    /// (<c>case FieldMask.A:</c>, <c>x is FieldMask.A</c>,
    /// switch-expression arms, constant-pattern matches).
    /// </summary>
    public required int ConsumedSwitchCount { get; init; }

    /// <summary>
    /// Convenience: <see cref="ProducedCount"/> == 0 AND
    /// <see cref="TotalReferences"/> &gt; 0. The state-machine
    /// drift signal — a value exists in the type, is checked for,
    /// but is never assigned to anything.
    /// </summary>
    public required bool IsUnproduced { get; init; }

    /// <summary>
    /// Convenience: <see cref="TotalReferences"/> == 0. Stricter than
    /// <see cref="IsUnproduced"/> — pure dead value.
    /// </summary>
    public required bool IsUnreferenced { get; init; }
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
