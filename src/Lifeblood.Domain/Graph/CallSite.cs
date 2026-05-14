namespace Lifeblood.Domain.Graph;

/// <summary>
/// Location and containing-symbol context for a graph edge's source occurrence.
/// When populated, identifies the exact <c>(file, line, column)</c> where the
/// edge's authoring expression appears and the canonical id of the enclosing
/// declaration (method / property / field / etc.) that lexically owns the
/// reference.
///
/// Dependency / dependants / find-references responses without CallSite
/// would return only <c>SourceId / TargetId</c> pairs, forcing callers to
/// fall back to manual file reading to answer "where and why does this
/// depend on X?". The structured shape lifts the information the adapter
/// already had at extraction time into the wire surface.
/// INV-EDGE-CALLSITE-001.
///
/// Nullable on <see cref="Edge.CallSite"/>: not every edge has a single
/// authoring location (module→module DependsOn, type→type Inherits, etc.).
/// When null, the edge is graph-derived rather than expression-derived and
/// callers should treat the absence as "not applicable" rather than "data
/// missing".
/// </summary>
public sealed class CallSite
{
    /// <summary>
    /// Repo-relative source-file path with forward-slash separators
    /// (e.g. <c>Assets/_Project/Scripts/.../Foo.cs</c>). Matches the format
    /// used by <see cref="SymbolKind.File"/> symbols' FilePath.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>One-based line number of the authoring expression's start.</summary>
    public required int Line { get; init; }

    /// <summary>One-based column number of the authoring expression's start.</summary>
    public required int Column { get; init; }

    /// <summary>One-based line number of the authoring expression's end.</summary>
    public int EndLine { get; init; }

    /// <summary>One-based column number of the authoring expression's end.</summary>
    public int EndColumn { get; init; }

    /// <summary>
    /// Canonical id of the lexically enclosing declaration (method, property
    /// getter / setter, field initializer expression, etc.). When the
    /// expression appears outside any method-like enclosure (a type-level
    /// edge inside the type declaration itself), this is the type id.
    /// Empty when the extractor could not resolve a containing symbol.
    /// </summary>
    public string ContainingSymbolId { get; init; } = "";
}
