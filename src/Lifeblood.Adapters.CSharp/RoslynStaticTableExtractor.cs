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
        var unwrapped = UnwrapTransparent(rowOp);

        string? ctorId = null;
        StaticTableCell[] cells = Array.Empty<StaticTableCell>();
        StaticTableValue? value = null;

        if (unwrapped is IObjectCreationOperation ctorOp && ctorOp.Constructor != null)
        {
            ctorId = buildSymbolId(ctorOp.Constructor);
            cells = BuildCells(ctorOp, buildSymbolId);
        }
        else
        {
            value = ClassifyValue(unwrapped, buildSymbolId);
        }

        return new StaticTableRow
        {
            Ordinal = ordinal,
            FilePath = span.Path ?? "",
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            ConstructorId = ctorId,
            Cells = cells,
            Value = value,
        };
    }

    /// <summary>
    /// Bind every <see cref="IArgumentOperation"/> on a constructor row
    /// to its corresponding parameter and classify the cell value.
    /// IArgumentOperation already routes named / positional / optional
    /// args to <see cref="IParameterSymbol"/>, so this method does not
    /// need to re-implement C# overload resolution — Roslyn surfaces
    /// the bound parameter on every argument operation.
    /// </summary>
    private static StaticTableCell[] BuildCells(IObjectCreationOperation ctorOp, Func<ISymbol, string> buildSymbolId)
    {
        var args = ctorOp.Arguments;
        if (args.IsDefaultOrEmpty) return Array.Empty<StaticTableCell>();
        var cells = new StaticTableCell[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var parameter = arg.Parameter;
            cells[i] = new StaticTableCell
            {
                ParameterName = parameter?.Name,
                Position = parameter?.Ordinal ?? i,
                ArgumentKind = MapArgumentKind(arg.ArgumentKind),
                Value = ClassifyValue(arg.Value, buildSymbolId),
            };
        }
        return cells;
    }

    /// <summary>
    /// Map <see cref="ArgumentKind"/> to the stable wire string. Names
    /// mirror the Roslyn enum so cross-version drift surfaces as a
    /// "unknown" fallback string the caller can still reason about.
    /// </summary>
    private static string MapArgumentKind(ArgumentKind kind) => kind switch
    {
        ArgumentKind.Explicit => StaticTableArgumentKind.Explicit,
        ArgumentKind.DefaultValue => StaticTableArgumentKind.DefaultValue,
        ArgumentKind.ParamArray => StaticTableArgumentKind.ParamArray,
        _ => kind.ToString(),
    };

    /// <summary>
    /// Classify a value-position operation into a
    /// <see cref="StaticTableValue"/>. The match table grows lego-by-lego;
    /// any shape not yet covered falls back to
    /// <see cref="StaticTableValueKind.Computed"/> with the raw source
    /// span as the eternal provenance. INV-EXTRACT-STATIC-TABLES-001.
    /// </summary>
    private static StaticTableValue ClassifyValue(IOperation op, Func<ISymbol, string> buildSymbolId)
    {
        _ = buildSymbolId;
        var inner = UnwrapTransparent(op);
        var span = inner.Syntax.GetLocation().GetLineSpan();
        var rawText = inner.Syntax.ToString();
        var filePath = span.Path ?? "";
        var line = span.StartLinePosition.Line + 1;
        var column = span.StartLinePosition.Character + 1;

        var methodGroupId = TryExtractMethodGroupId(inner, buildSymbolId);
        if (methodGroupId != null)
        {
            return new StaticTableValue
            {
                Kind = StaticTableValueKind.MethodGroup,
                RawText = rawText,
                FilePath = filePath, Line = line, Column = column,
                MethodGroupId = methodGroupId,
            };
        }

        if (inner is IFieldReferenceOperation fieldRef
            && fieldRef.Field.ContainingType?.TypeKind == TypeKind.Enum
            && fieldRef.Field.IsConst)
        {
            return new StaticTableValue
            {
                Kind = StaticTableValueKind.EnumMember,
                RawText = rawText,
                FilePath = filePath, Line = line, Column = column,
                EnumMemberId = buildSymbolId(fieldRef.Field),
            };
        }

        if (inner is IBinaryOperation binary && binary.OperatorKind == BinaryOperatorKind.Or)
        {
            var flagIds = new List<string>();
            if (TryCollectEnumFlagMembers(binary, flagIds, buildSymbolId))
            {
                return new StaticTableValue
                {
                    Kind = StaticTableValueKind.EnumFlags,
                    RawText = rawText,
                    FilePath = filePath, Line = line, Column = column,
                    EnumFlagMemberIds = flagIds.ToArray(),
                };
            }
        }

        if (inner is ILiteralOperation literal && literal.ConstantValue.HasValue)
        {
            var constant = literal.ConstantValue.Value;
            if (constant == null)
            {
                return new StaticTableValue
                {
                    Kind = StaticTableValueKind.Null,
                    RawText = rawText,
                    FilePath = filePath,
                    Line = line,
                    Column = column,
                };
            }
            switch (constant)
            {
                case bool b:
                    return new StaticTableValue
                    {
                        Kind = StaticTableValueKind.Bool,
                        RawText = rawText,
                        FilePath = filePath, Line = line, Column = column,
                        BoolValue = b,
                    };
                case string s:
                    return new StaticTableValue
                    {
                        Kind = StaticTableValueKind.String,
                        RawText = rawText,
                        FilePath = filePath, Line = line, Column = column,
                        StringValue = s,
                    };
            }
            if (IsNumericPrimitive(constant))
            {
                return new StaticTableValue
                {
                    Kind = StaticTableValueKind.Number,
                    RawText = rawText,
                    FilePath = filePath, Line = line, Column = column,
                    NumberValue = Convert.ToDouble(constant, System.Globalization.CultureInfo.InvariantCulture),
                };
            }
        }

        // Roslyn surfaces a bare `null` literal in a non-nullable typed
        // context as a default-literal / conversion combo. Cover the
        // common case by checking the constant-value flag on the
        // outermost original op too.
        if (op.ConstantValue.HasValue && op.ConstantValue.Value == null)
        {
            return new StaticTableValue
            {
                Kind = StaticTableValueKind.Null,
                RawText = rawText,
                FilePath = filePath, Line = line, Column = column,
            };
        }

        return new StaticTableValue
        {
            Kind = StaticTableValueKind.Computed,
            RawText = rawText,
            FilePath = filePath, Line = line, Column = column,
        };
    }

    private static bool IsNumericPrimitive(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// Resolve a method-group cell value to its target method id when
    /// the operation is a delegate creation over an IMethodReferenceOperation.
    /// Returns null for inline lambdas / anonymous functions — the
    /// extractor does not peek inside delegate bodies (that is dataflow,
    /// a separate truth tier). Inline lambdas fall back to Computed.
    /// </summary>
    private static string? TryExtractMethodGroupId(IOperation op, Func<ISymbol, string> buildSymbolId)
    {
        if (op is IDelegateCreationOperation del)
        {
            var inner = UnwrapTransparent(del.Target);
            if (inner is IMethodReferenceOperation methodRef)
                return buildSymbolId(methodRef.Method);
        }
        if (op is IMethodReferenceOperation directRef)
            return buildSymbolId(directRef.Method);
        return null;
    }

    /// <summary>
    /// Flatten an <c>|</c>-composed enum-flag expression into its leaf
    /// member ids in authoring order. Recursion descends both operands;
    /// each leaf must be an enum-const <see cref="IFieldReferenceOperation"/>
    /// or the whole expression is rejected (returns false) so the
    /// caller can fall back to <see cref="StaticTableValueKind.Computed"/>.
    /// </summary>
    private static bool TryCollectEnumFlagMembers(IOperation op, List<string> flagIds, Func<ISymbol, string> buildSymbolId)
    {
        var inner = UnwrapTransparent(op);
        if (inner is IBinaryOperation binary && binary.OperatorKind == BinaryOperatorKind.Or)
        {
            return TryCollectEnumFlagMembers(binary.LeftOperand, flagIds, buildSymbolId)
                && TryCollectEnumFlagMembers(binary.RightOperand, flagIds, buildSymbolId);
        }
        if (inner is IFieldReferenceOperation fieldRef
            && fieldRef.Field.ContainingType?.TypeKind == TypeKind.Enum
            && fieldRef.Field.IsConst)
        {
            flagIds.Add(buildSymbolId(fieldRef.Field));
            return true;
        }
        return false;
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
