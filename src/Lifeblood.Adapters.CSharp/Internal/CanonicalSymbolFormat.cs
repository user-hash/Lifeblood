using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Owns declaration identity across source, metadata and bound call sites.
/// Legacy v1 uses pinned display signatures. Version 2 includes assembly identity,
/// declaring-type arity, member metadata name, passing modes, method arity and
/// conversion return type. Constructed and reduced symbols address declarations.
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
    public static string BuildSymbolId(ISymbol symbol, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
    {
        _ = UseVersion2(version);
        return symbol switch
        {
            IMethodSymbol method => BuildMethodId(method, version),
            INamedTypeSymbol type => BuildTypeId(type, version),
            IFieldSymbol field => BuildFieldId(field, version),
            IPropertySymbol property => BuildPropertyId(property, version),
            IEventSymbol evt => BuildEventId(evt, version),
            INamespaceSymbol ns => SymbolIds.Namespace(ns.ToDisplayString()),
            _ => $"unknown:{symbol.ToDisplayString()}",
        };
    }

    /// <summary>
    /// Build a canonical method ID, including extension-method reduced-form
    /// and constructed-generic normalization.
    /// </summary>
    public static string BuildMethodId(IMethodSymbol method, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
    {
        if (method.ReducedFrom != null) method = method.ReducedFrom;
        method = method.OriginalDefinition;

        if (UseVersion2(version) && method.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction)
            return UnsupportedSymbolId(method);

        // Accessor methods are not graph symbols. Route them to their owning
        // property/event so accessor-body edges never dangle.
        if (method.AssociatedSymbol is IPropertySymbol property)
            return BuildPropertyId(property, version);
        if (method.AssociatedSymbol is IEventSymbol evt)
            return BuildEventId(evt, version);

        if (UseVersion2(version))
            return BuildVersion2Id("method", method, $"{method.Arity};{method.IsVararg};"
                + ParameterIdentity(method.Parameters)
                + (method.MethodKind == MethodKind.Conversion ? Pack(TypeIdentity(method.ReturnType)) : ""));

        return SymbolIds.Method(
            GetFullName(method.ContainingType),
            method.Name,
            BuildParamSignature(method));
    }

    public static string BuildTypeId(INamedTypeSymbol type, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
        => UseVersion2(version)
            ? BuildVersion2Id("type", type.OriginalDefinition, "")
            : SymbolIds.Type(GetFullName(type.OriginalDefinition));

    public static string BuildFieldId(IFieldSymbol field, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
        => UseVersion2(version)
            ? BuildVersion2Id("field", field.OriginalDefinition, "")
            : SymbolIds.Field(GetFullName(field.ContainingType), field.Name);

    public static string BuildPropertyId(IPropertySymbol property, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
    {
        property = property.OriginalDefinition;
        if (UseVersion2(version))
            return BuildVersion2Id("property", property, "property;" + ParameterIdentity(property.Parameters));
        var containingType = GetFullName(property.ContainingType);
        return property.IsIndexer
            ? SymbolIds.Property(containingType, $"this[{BuildIndexerParamSignature(property)}]")
            : SymbolIds.Property(containingType, property.Name);
    }

    public static string BuildEventId(IEventSymbol evt, CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
    {
        evt = evt.OriginalDefinition;
        if (UseVersion2(version))
            return BuildVersion2Id("property", evt, "event");
        return SymbolIds.Property(GetFullName(evt.ContainingType), evt.Name);
    }

    private static bool UseVersion2(CanonicalSymbolIdVersion version)
        => version switch
        {
            CanonicalSymbolIdVersion.LegacyV1 => false,
            CanonicalSymbolIdVersion.AssemblyQualifiedV2 => true,
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

    // Components are escaped independently. Length prefixes inside the signature
    // keep nested type arguments and parameter boundaries unambiguous.
    private static string BuildVersion2Id(string kind, ISymbol symbol, string signature)
    {
        var owner = (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.OriginalDefinition;
        var declaration = owner?.GetDocumentationCommentId();
        if (declaration == null || owner is { IsAnonymousType: true } or { TypeKind: TypeKind.Error }
            || symbol.ContainingAssembly == null)
            return UnsupportedSymbolId(symbol);
        return $"{kind}:v2|{Uri.EscapeDataString(symbol.ContainingAssembly.Identity.ToString())}"
            + $"|{Uri.EscapeDataString(declaration)}"
            + $"|{Uri.EscapeDataString(symbol is INamedTypeSymbol ? "" : symbol.MetadataName)}"
            + $"|{Uri.EscapeDataString(signature)}";
    }

    private static string Pack(string value) => $"{value.Length}:{value}";

    // Compiler-created or unresolved symbols are observations, not declaration
    // addresses. The prefix prevents exact declaration lookup from accepting them.
    private static string UnsupportedSymbolId(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return "unknown:v2|" + Uri.EscapeDataString(symbol.ToDisplayString(ParamType))
            + "|" + Uri.EscapeDataString(symbol.ContainingAssembly?.Identity.ToString() ?? "")
            + "|" + Uri.EscapeDataString(location?.SourceTree?.FilePath ?? "")
            + $"|{location?.SourceSpan.Start ?? -1}";
    }

    private static string ParameterIdentity(IEnumerable<IParameterSymbol> parameters)
        => string.Concat(parameters.Select(p => Pack($"{(int)p.RefKind};{TypeIdentity(p.Type)}")));

    private static string TypeIdentity(ITypeSymbol type)
    {
        if (type is IErrorTypeSymbol error)
            return "e" + Pack(error.ToDisplayString(ParamType))
                + string.Concat(error.TypeArguments.Select(t => Pack(TypeIdentity(t))));
        if (type.TypeKind == TypeKind.Dynamic) return $"s{(int)SpecialType.System_Object}";
        if (type.SpecialType != SpecialType.None && type is not INamedTypeSymbol { Arity: > 0 })
            return $"s{(int)type.SpecialType}";
        return type switch
        {
            IArrayTypeSymbol array => $"a{array.Rank};{Pack(TypeIdentity(array.ElementType))}",
            IPointerTypeSymbol pointer => "p" + Pack(TypeIdentity(pointer.PointedAtType)),
            IFunctionPointerTypeSymbol pointer => $"f{(int)pointer.Signature.CallingConvention};"
                + string.Concat(pointer.Signature.UnmanagedCallingConventionTypes.Select(t => Pack(TypeIdentity(t))))
                + $";{(int)pointer.Signature.RefKind};{Pack(TypeIdentity(pointer.Signature.ReturnType))}"
                + ParameterIdentity(pointer.Signature.Parameters),
            ITypeParameterSymbol parameter => parameter.TypeParameterKind == TypeParameterKind.Method
                ? $"m{parameter.Ordinal}"
                : $"t{Pack(parameter.ContainingType.OriginalDefinition.GetDocumentationCommentId()!)}{parameter.Ordinal}",
            INamedTypeSymbol named => NamedTypeIdentity(named.TupleUnderlyingType ?? named),
            _ => throw new ArgumentException("The parameter type has no canonical declaration identity.", nameof(type)),
        };
    }

    private static string NamedTypeIdentity(INamedTypeSymbol type)
        => "n" + Pack(type.ContainingAssembly.Identity.ToString())
            + Pack(type.OriginalDefinition.GetDocumentationCommentId()
                ?? throw new ArgumentException("The type has no declaration identity.", nameof(type)))
            + (type.ContainingType == null ? "" : Pack(TypeIdentity(type.ContainingType)))
            + string.Concat(type.TypeArguments.Select(t => Pack(TypeIdentity(t))));

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
