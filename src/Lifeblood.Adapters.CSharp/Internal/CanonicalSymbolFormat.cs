using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Single source of truth for parameter-type display strings used in Lifeblood method
/// symbol IDs. EVERY builder that emits a method ID — RoslynSymbolExtractor, RoslynEdgeExtractor,
/// RoslynCompilationHost.BuildSymbolId, RoslynWorkspaceManager.FindInCompilation — must route
/// through this formatter so that the same C# symbol always produces the same ID, regardless of
/// whether the symbol comes from source or metadata, and regardless of which Roslyn version is loaded.
///
/// Why this exists: Roslyn's <c>ITypeSymbol.ToDisplayString()</c> default is
/// <c>SymbolDisplayFormat.CSharpErrorMessageFormat</c>, which is stable enough in practice but is
/// (1) implicitly inherited from Roslyn rather than owned by Lifeblood, and (2) carries a few
/// context-sensitive behaviors (nullability, tuple formatting, expand-nullable) that have shifted
/// across Roslyn releases. Pinning the format here makes Lifeblood's symbol ID grammar
/// independent of those choices.
///
/// Format choices:
///   - Omit the <c>global::</c> prefix — IDs are namespace-rooted, not assembly-rooted.
///   - Fully qualify type names (NameAndContainingTypesAndNamespaces) so two distinct types
///     with the same short name in different namespaces never collide.
///   - Include generic type parameters (List&lt;T&gt;, not List).
///   - Use C# special-type aliases (<c>int</c> not <c>System.Int32</c>) so IDs match what
///     developers actually write in source.
///   - Do NOT emit nullability annotations — <c>string?</c> and <c>string</c> share an ID
///     because the underlying method symbol is the same.
/// </summary>
internal static class CanonicalSymbolFormat
{
    /// <summary>
    /// Pinned <see cref="SymbolDisplayFormat"/> for parameter-type display strings.
    /// Do not call <c>ITypeSymbol.ToDisplayString()</c> without passing this format
    /// from anywhere that builds a Lifeblood method symbol ID.
    /// </summary>
    public static readonly SymbolDisplayFormat ParamType = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>
    /// Build the comma-separated parameter type list for a method symbol.
    /// </summary>
    public static string BuildParamSignature(IMethodSymbol method)
        => string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString(ParamType)));

    /// <summary>
    /// Build the comma-separated parameter type list for an indexer property symbol.
    /// </summary>
    public static string BuildIndexerParamSignature(IPropertySymbol indexer)
        => string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString(ParamType)));
}
