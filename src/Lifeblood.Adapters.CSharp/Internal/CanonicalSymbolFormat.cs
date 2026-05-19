using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Single source of truth for Roslyn-symbol to Lifeblood-ID formatting.
/// Declaration extraction, edge extraction, and compilation-host lookup all route
/// through this helper so the same C# symbol produces the same ID regardless
/// of whether it came from source, metadata, or a call-site-bound Roslyn symbol.
///
/// Why this exists: Roslyn's <c>ITypeSymbol.ToDisplayString()</c> default is
/// <c>SymbolDisplayFormat.CSharpErrorMessageFormat</c>, which is stable enough in practice but is
/// (1) implicitly inherited from Roslyn rather than owned by Lifeblood, and (2) carries a few
/// context-sensitive behaviors (nullability, tuple formatting, expand-nullable) that have shifted
/// across Roslyn releases. Pinning the format here makes Lifeblood's symbol ID grammar
/// independent of those choices.
///
/// Format choices:
///   - Omit the <c>global::</c> prefix - IDs are namespace-rooted, not assembly-rooted.
///   - Fully qualify parameter type names (NameAndContainingTypesAndNamespaces) so two distinct
///     parameter types with the same short name in different namespaces never collide.
///   - Include generic type parameters in parameter signatures (List&lt;T&gt;, not List).
///   - Use C# special-type aliases in parameter signatures (<c>int</c> not
///     <c>System.Int32</c>) so IDs match what developers actually write in source.
///   - Do NOT emit nullability annotations - <c>string?</c> and <c>string</c> share an ID
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

    /// <summary>
    /// Build the canonical Lifeblood symbol ID for any Roslyn symbol shape the
    /// C# adapter persists in the graph.
    /// </summary>
    public static string BuildSymbolId(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol method => BuildMethodId(method),
            INamedTypeSymbol type => BuildTypeId(type),
            IFieldSymbol field => BuildFieldId(field),
            IPropertySymbol property => BuildPropertyId(property),
            IEventSymbol evt => BuildEventId(evt),
            INamespaceSymbol ns => SymbolIds.Namespace(ns.ToDisplayString()),
            _ => $"unknown:{symbol.ToDisplayString()}",
        };

    /// <summary>
    /// Build a canonical method ID, including extension-method reduced-form
    /// and constructed-generic normalization.
    /// </summary>
    public static string BuildMethodId(IMethodSymbol method)
    {
        if (method.ReducedFrom != null) method = method.ReducedFrom;
        method = method.OriginalDefinition;

        // Accessor methods are not graph symbols. Route them to their owning
        // property/event so accessor-body edges never dangle.
        if (method.AssociatedSymbol is IPropertySymbol property)
            return BuildPropertyId(property);
        if (method.AssociatedSymbol is IEventSymbol evt)
            return BuildEventId(evt);

        return SymbolIds.Method(
            GetFullName(method.ContainingType),
            method.Name,
            BuildParamSignature(method));
    }

    public static string BuildTypeId(INamedTypeSymbol type)
        => SymbolIds.Type(GetFullName(type.OriginalDefinition));

    public static string BuildFieldId(IFieldSymbol field)
        => SymbolIds.Field(GetFullName(field.ContainingType), field.Name);

    public static string BuildPropertyId(IPropertySymbol property)
    {
        property = property.OriginalDefinition;
        var containingType = GetFullName(property.ContainingType);
        return property.IsIndexer
            ? SymbolIds.Property(containingType, $"this[{BuildIndexerParamSignature(property)}]")
            : SymbolIds.Property(containingType, property.Name);
    }

    public static string BuildEventId(IEventSymbol evt)
    {
        evt = evt.OriginalDefinition;
        return SymbolIds.Property(GetFullName(evt.ContainingType), evt.Name);
    }

    public static string GetFullName(ISymbol symbol)
    {
        var parts = new List<string>();
        var current = symbol;
        while (current != null && current is not INamespaceSymbol { IsGlobalNamespace: true })
        {
            if (current is INamespaceSymbol ns && !string.IsNullOrEmpty(ns.Name))
                parts.Add(ns.Name);
            else if (current is INamedTypeSymbol or IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol)
                parts.Add(current.Name);

            current = current.ContainingSymbol;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
