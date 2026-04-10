using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Provides compilation-level capabilities: diagnostics, compile-checking, reference finding.
/// Language-agnostic contract — Roslyn implements with CSharpCompilation.
/// </summary>
public interface ICompilationHost
{
    bool IsAvailable { get; }
    DiagnosticInfo[] GetDiagnostics(string? moduleName = null);
    CompileCheckResult CompileCheck(string code, string? moduleName = null);

    /// <summary>
    /// Find every source location that references the given symbol. Defaults
    /// to references only — declaration sites are excluded unless the caller
    /// opts in via <see cref="FindReferences(string, FindReferencesOptions)"/>.
    /// </summary>
    ReferenceLocation[] FindReferences(string symbolId);

    /// <summary>
    /// Find references with explicit operation policy
    /// (<see cref="FindReferencesOptions.IncludeDeclarations"/> etc.).
    /// "Include declarations or not" is a write-side reference-search policy
    /// — it is NOT something the resolver can decide from a symbol id alone.
    /// See LB-FR-003 / Plan v4 §2.6 / Correction 2 from the external review.
    /// </summary>
    ReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options);

    /// <summary>Find where a symbol is declared (definition site).</summary>
    DefinitionLocation? FindDefinition(string symbolId);

    /// <summary>Find all types that implement an interface or override a virtual member.</summary>
    string[] FindImplementations(string symbolId);

    /// <summary>Resolve the symbol at a source position (file + line + column).</summary>
    SymbolAtPosition? GetSymbolAtPosition(string filePath, int line, int column);

    /// <summary>Get XML documentation for a symbol.</summary>
    string GetDocumentation(string symbolId);
}

/// <summary>
/// Options for <see cref="ICompilationHost.FindReferences(string, FindReferencesOptions)"/>.
/// Each flag is a deliberate behavior choice on the live-walker side, NOT
/// something the resolver can decide from a symbol id alone.
///
/// Reserved as a separate record so future per-call policy (filter scope,
/// match case, declaration-only mode, etc.) can be added without further
/// signature changes.
/// </summary>
public sealed class FindReferencesOptions
{
    public static readonly FindReferencesOptions Default = new();

    /// <summary>
    /// When true, the result includes a synthetic <c>"(declaration)"</c>
    /// entry for every source location where the symbol is declared. For
    /// partial types, this means one entry per partial declaration file
    /// (Roslyn's <c>ISymbol.Locations</c> returns one location per partial).
    /// For non-partial symbols, exactly one declaration entry. Default
    /// false preserves the existing pure-references-only behavior.
    /// </summary>
    public bool IncludeDeclarations { get; init; }
}
