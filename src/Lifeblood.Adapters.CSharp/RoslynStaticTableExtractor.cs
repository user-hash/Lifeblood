using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Generic static-initializer table extractor. Walks each <c>static</c>
/// field / property declaration on a target type, asks Roslyn for the
/// initializer's <see cref="IOperation"/> tree, and classifies row
/// constructors + cell values into <see cref="StaticTableReport"/>
/// facts. The extractor is intentionally consumer-domain-blind — any
/// vocabulary outside the canonical kind set in
/// <see cref="StaticTableValueKind"/> is forbidden and pinned by the
/// name-leakage ratchet test. INV-EXTRACT-STATIC-TABLES-001.
/// </summary>
internal static class RoslynStaticTableExtractor
{
    internal const int DefaultMaxRows = 1024;
    internal const int DefaultMaxTables = 64;

    internal static StaticTableReport? Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        INamedTypeSymbol typeSymbol,
        string typeId,
        StaticTablesOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        var maxRows = ClampPositive(options.MaxRows, DefaultMaxRows);
        var maxTables = ClampPositive(options.MaxTables, DefaultMaxTables);
        var memberFilter = options.MemberName;

        // ResolveFromSource may return a workspace-owned symbol whose
        // syntax references live in a different tree than the retained
        // _compilations. Re-discover the type by metadata-name lookup
        // inside each retained compilation so member iteration walks
        // the trees the host actually owns. INV-EXTRACT-STATIC-TABLES-001.
        var owning = FindOwningCompilation(compilations, typeSymbol);
        if (owning == null)
        {
            return new StaticTableReport
            {
                TypeId = typeId,
                Tables = Array.Empty<StaticTable>(),
                TablesTruncated = false,
            };
        }
        var (moduleName, compilation, localType) = owning.Value;

        var tables = new List<StaticTable>();
        var tablesTruncated = false;

        foreach (var member in localType.GetMembers())
        {
            if (!member.IsStatic) continue;
            if (member.Kind != SymbolKind.Field && member.Kind != SymbolKind.Property) continue;
            if (memberFilter != null && !string.Equals(memberFilter, member.Name, StringComparison.Ordinal)) continue;

            var initializer = TryGetInitializerExpression(member);
            if (initializer == null) continue;

            var tree = initializer.SyntaxTree;
            var model = compilation.GetSemanticModel(tree);
            var rootOp = model.GetOperation(initializer);
            if (rootOp == null) continue;

            var classified = ClassifyContainer(rootOp);
            if (classified == null) continue;

            if (tables.Count >= maxTables)
            {
                tablesTruncated = true;
                break;
            }

            var (containerKind, rowOps, elementTypeSymbol) = classified.Value;

            var rowsTruncated = false;
            var rowSlots = new List<StaticTableRow>(Math.Min(rowOps.Count, maxRows));
            for (var i = 0; i < rowOps.Count; i++)
            {
                if (i >= maxRows)
                {
                    rowsTruncated = true;
                    break;
                }
                rowSlots.Add(BuildRow(i, rowOps[i], buildSymbolId));
            }

            var memberSpan = initializer.GetLocation().GetLineSpan();
            tables.Add(new StaticTable
            {
                MemberId = buildSymbolId(member),
                MemberName = member.Name,
                FilePath = memberSpan.Path ?? "",
                Line = memberSpan.StartLinePosition.Line + 1,
                Column = memberSpan.StartLinePosition.Character + 1,
                ModuleName = moduleName,
                ContainerKind = containerKind,
                ElementTypeId = elementTypeSymbol != null ? buildSymbolId(elementTypeSymbol) : null,
                Rows = rowSlots.ToArray(),
                RowsTruncated = rowsTruncated,
            });
        }

        return new StaticTableReport
        {
            TypeId = typeId,
            Tables = tables.ToArray(),
            TablesTruncated = tablesTruncated,
        };
    }

    /// <summary>
    /// Resolve the initializer expression syntax for a static field or
    /// property. Covers three shapes: plain field initializer, property
    /// with <c>= …;</c> initializer, expression-bodied property
    /// (<c>=&gt; …;</c>). Returns null when the member declares no
    /// initializer in source.
    /// </summary>
    private static ExpressionSyntax? TryGetInitializerExpression(ISymbol member)
    {
        foreach (var syntaxRef in member.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax();
            switch (node)
            {
                case VariableDeclaratorSyntax v when v.Initializer != null:
                    return v.Initializer.Value;
                case PropertyDeclarationSyntax p when p.Initializer != null:
                    return p.Initializer.Value;
                case PropertyDeclarationSyntax p2 when p2.ExpressionBody != null:
                    return p2.ExpressionBody.Expression;
            }
        }
        return null;
    }

    /// <summary>
    /// Classify the initializer's root operation into a container shape
    /// + row-operation list + (optional) element-type symbol. Returns
    /// null when the operation tree is not a recognised table shape, so
    /// non-table static initializers (scalar literals, expression
    /// chains, etc.) are silently skipped.
    /// </summary>
    private static (string Kind, IReadOnlyList<IOperation> Rows, ITypeSymbol? ElementType)? ClassifyContainer(IOperation rootOp)
    {
        var op = UnwrapTransparent(rootOp);

        if (op is IArrayCreationOperation array)
        {
            var elems = array.Initializer?.ElementValues ?? (IReadOnlyList<IOperation>)Array.Empty<IOperation>();
            return (StaticTableContainerKind.Array, elems, (array.Type as IArrayTypeSymbol)?.ElementType);
        }

        var collectionElems = TryUnpackCollectionExpression(op, out var collectionElemType);
        if (collectionElems != null)
        {
            return (StaticTableContainerKind.CollectionExpression, collectionElems, collectionElemType);
        }

        if (op is IObjectCreationOperation obj)
        {
            return (StaticTableContainerKind.ObjectCreation, new[] { (IOperation)obj }, obj.Type);
        }

        return null;
    }

    /// <summary>
    /// Strip Roslyn's bookkeeping wrappers (implicit conversion, etc.)
    /// so container pattern-matching sees the authoring shape.
    /// </summary>
    private static IOperation UnwrapTransparent(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation conv when conv.IsImplicit && conv.Operand != null:
                    op = conv.Operand;
                    continue;
                case IParenthesizedOperation paren when paren.Operand != null:
                    op = paren.Operand;
                    continue;
                default:
                    return op;
            }
        }
    }

    /// <summary>
    /// Detect a C# 12 collection-expression operation without taking a
    /// compile-time dependency on the matching public interface
    /// (introduced in a specific Roslyn version). Uses the runtime
    /// type's well-known name to stay compatible across Roslyn
    /// upgrades.
    /// </summary>
    private static IReadOnlyList<IOperation>? TryUnpackCollectionExpression(IOperation op, out ITypeSymbol? elementType)
    {
        elementType = null;
        var opType = op.GetType();
        if (opType.Name != "CollectionExpressionOperation" && !opType.GetInterfaces().Any(i => i.Name == "ICollectionExpressionOperation"))
            return null;
        var elementsProp = opType.GetProperty("Elements");
        if (elementsProp?.GetValue(op) is not IEnumerable<IOperation> elements) return null;
        var list = elements.ToArray();
        if (op.Type is IArrayTypeSymbol arr) elementType = arr.ElementType;
        else if (op.Type is INamedTypeSymbol named && named.TypeArguments.Length == 1) elementType = named.TypeArguments[0];
        return list;
    }

    private static StaticTableRow BuildRow(int ordinal, IOperation rowOp, Func<ISymbol, string> buildSymbolId)
    {
        var span = rowOp.Syntax.GetLocation().GetLineSpan();
        string? ctorId = null;
        if (UnwrapTransparent(rowOp) is IObjectCreationOperation ctorOp && ctorOp.Constructor != null)
        {
            ctorId = buildSymbolId(ctorOp.Constructor);
        }
        return new StaticTableRow
        {
            Ordinal = ordinal,
            FilePath = span.Path ?? "",
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            ConstructorId = ctorId,
            Cells = Array.Empty<StaticTableCell>(),
        };
    }

    /// <summary>
    /// Find which retained compilation owns the source declaration for
    /// <paramref name="typeSymbol"/>. Returns the matching local
    /// <see cref="INamedTypeSymbol"/> from that compilation so member
    /// iteration walks syntax trees the compilation actually owns —
    /// reference equality on <see cref="SyntaxTree"/> instances is
    /// unsafe because <c>ResolveFromSource</c> may return a workspace-
    /// owned symbol with re-loaded trees.
    /// </summary>
    private static (string Module, CSharpCompilation Compilation, INamedTypeSymbol LocalType)? FindOwningCompilation(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        INamedTypeSymbol typeSymbol)
    {
        var lookupName = BuildMetadataLookupName(typeSymbol);
        if (lookupName != null)
        {
            foreach (var kv in compilations)
            {
                var local = kv.Value.GetTypeByMetadataName(lookupName);
                if (local != null && local.Locations.Any(l => l.IsInSource))
                    return (kv.Key, kv.Value, local);
            }
        }
        // Fallback: linear scan by SymbolEqualityComparer (covers
        // generic-arity edge cases the metadata-name path misses).
        foreach (var kv in compilations)
        {
            foreach (var src in kv.Value.GlobalNamespace.GetAllNamedTypes())
            {
                if (SymbolEqualityComparer.Default.Equals(src, typeSymbol))
                    return (kv.Key, kv.Value, src);
            }
        }
        return null;
    }

    /// <summary>
    /// Build the metadata-name lookup string for
    /// <see cref="CSharpCompilation.GetTypeByMetadataName"/>. Walks the
    /// containing-type chain joining with <c>+</c> (CLR nested-type
    /// separator), prefixes with the containing namespace joined by
    /// <c>.</c>. Returns null when the type is anonymous or in the
    /// global namespace without a metadata name.
    /// </summary>
    private static string? BuildMetadataLookupName(INamedTypeSymbol typeSymbol)
    {
        if (string.IsNullOrEmpty(typeSymbol.MetadataName)) return null;
        var typeChain = new List<string>();
        for (var t = typeSymbol; t != null; t = t.ContainingType)
            typeChain.Insert(0, t.MetadataName);
        var typePart = string.Join("+", typeChain);
        var ns = typeSymbol.ContainingNamespace;
        if (ns == null || ns.IsGlobalNamespace) return typePart;
        var nsName = ns.ToDisplayString();
        return string.IsNullOrEmpty(nsName) ? typePart : nsName + "." + typePart;
    }

    private static int ClampPositive(int? requested, int @default)
    {
        if (!requested.HasValue) return @default;
        return requested.Value > 0 ? requested.Value : @default;
    }
}

internal static class NamespaceWalkExtensions
{
    /// <summary>
    /// Depth-first walk of all named types under a namespace. Used by
    /// the fallback path in <see cref="RoslynStaticTableExtractor"/>
    /// when metadata-name lookup misses (anonymous-like edge cases).
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(this INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol inner)
            {
                foreach (var t in inner.GetAllNamedTypes()) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in NestedTypes(type)) yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested)) yield return deeper;
        }
    }
}
