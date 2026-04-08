using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Extracts dependency edges from Roslyn's semantic model.
/// Inherits, Implements, Calls, References — all with semantic Evidence.
/// Handles: inheritance, interfaces, method calls, constructor calls, member access,
/// type references, generic type arguments, typeof() expressions, attribute types.
/// </summary>
public sealed class RoslynEdgeExtractor
{
    private static readonly Evidence SemanticEvidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "Roslyn",
        Confidence = ConfidenceLevel.Proven,
    };

    public List<Edge> Extract(SemanticModel model, SyntaxNode root)
    {
        var edges = new List<Edge>();
        var seen = new HashSet<(string, string, EdgeKind)>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax typeDecl:
                    ExtractInheritanceEdges(model, typeDecl, edges, seen);
                    break;

                case InvocationExpressionSyntax invocation:
                    ExtractCallEdge(model, invocation, edges, seen);
                    break;

                case ObjectCreationExpressionSyntax creation:
                    ExtractConstructorCallEdge(model, creation, edges, seen);
                    break;

                case IdentifierNameSyntax identifier:
                    ExtractReferenceEdge(model, identifier, edges, seen);
                    break;

                case MemberAccessExpressionSyntax memberAccess:
                    ExtractMemberAccessEdge(model, memberAccess, edges, seen);
                    break;

                case GenericNameSyntax genericName:
                    ExtractGenericTypeArgEdges(model, genericName, edges, seen);
                    break;

                case TypeOfExpressionSyntax typeofExpr:
                    ExtractTypeOfEdge(model, typeofExpr, edges, seen);
                    break;

                case AttributeSyntax attribute:
                    ExtractAttributeEdge(model, attribute, edges, seen);
                    break;
            }
        }

        return edges;
    }

    private void ExtractInheritanceEdges(
        SemanticModel model, TypeDeclarationSyntax typeDecl,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var typeSymbol = model.GetDeclaredSymbol(typeDecl);
        if (typeSymbol == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(typeSymbol));

        // Base type → Inherits (only source-defined bases)
        if (typeSymbol.BaseType != null
            && typeSymbol.BaseType.SpecialType != SpecialType.System_Object
            && typeSymbol.BaseType.SpecialType != SpecialType.System_ValueType
            && typeSymbol.BaseType.SpecialType != SpecialType.System_Enum
            && IsFromSource(typeSymbol.BaseType))
        {
            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(typeSymbol.BaseType));
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.Inherits);
        }

        // Interfaces → Implements (only source-defined interfaces)
        foreach (var iface in typeSymbol.Interfaces)
        {
            if (!IsFromSource(iface)) continue;
            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(iface));
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.Implements);
        }
    }

    private void ExtractCallEdge(
        SemanticModel model, InvocationExpressionSyntax invocation,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(invocation);
        var target = symbolInfo.Symbol as IMethodSymbol;
        if (target?.ContainingType == null) return;
        if (!IsFromSource(target)) return;

        var caller = FindContainingMethodOrLocal(model, invocation);
        if (caller == null) return;

        var sourceId = GetMethodId(caller);
        var paramSig = string.Join(",", target.Parameters.Select(p => p.Type.ToDisplayString()));
        var targetId = SymbolIds.Method(
            RoslynSymbolExtractor.GetFullName(target.ContainingType),
            target.Name, paramSig);

        AddEdge(edges, seen, sourceId, targetId, EdgeKind.Calls);
    }

    private void ExtractConstructorCallEdge(
        SemanticModel model, ObjectCreationExpressionSyntax creation,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(creation);
        var target = symbolInfo.Symbol as IMethodSymbol;
        if (target?.ContainingType == null) return;
        if (!IsFromSource(target.ContainingType)) return;

        var caller = FindContainingMethodOrLocal(model, creation);
        if (caller == null) return;

        var sourceId = GetMethodId(caller);
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));
        AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    private void ExtractReferenceEdge(
        SemanticModel model, IdentifierNameSyntax identifier,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        // Skip identifiers that are part of type declarations (class/struct/enum name)
        // Note: method return types ARE IdentifierNameSyntax with parent MethodDeclarationSyntax —
        // we intentionally DO extract those as References edges.
        if (identifier.Parent is BaseTypeDeclarationSyntax) return;

        var symbolInfo = model.GetSymbolInfo(identifier);
        var referencedSymbol = symbolInfo.Symbol;
        if (referencedSymbol == null) return;

        // Only track references to source-defined types
        if (referencedSymbol is not INamedTypeSymbol referencedType) return;
        if (!IsFromSource(referencedType)) return;

        var containingType = FindContainingType(model, identifier);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(referencedType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    private void ExtractMemberAccessEdge(
        SemanticModel model, MemberAccessExpressionSyntax memberAccess,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(memberAccess);
        var target = symbolInfo.Symbol;
        if (target?.ContainingType == null) return;
        if (!IsFromSource(target.ContainingType)) return;

        var containingType = FindContainingType(model, memberAccess);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Extract References edges for generic type arguments.
    /// List&lt;IRepository&gt; creates a dependency on IRepository.
    /// Task&lt;AnalysisResult&gt; creates a dependency on AnalysisResult.
    /// Covers Dictionary&lt;K,V&gt;, IEnumerable&lt;T&gt;, etc.
    /// </summary>
    private void ExtractGenericTypeArgEdges(
        SemanticModel model, GenericNameSyntax genericName,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var containingType = FindContainingType(model, genericName);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));

        foreach (var typeArg in genericName.TypeArgumentList.Arguments)
        {
            var typeInfo = model.GetTypeInfo(typeArg);
            var argType = typeInfo.Type as INamedTypeSymbol;
            if (argType == null) continue;
            if (!IsFromSource(argType)) continue;

            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(argType));
            if (sourceId != targetId)
                AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
        }
    }

    /// <summary>
    /// Extract References edges for typeof() expressions.
    /// typeof(MyHandler) creates a dependency on MyHandler.
    /// Common in attribute arguments: [ServiceFilter(typeof(MyFilter))]
    /// </summary>
    private void ExtractTypeOfEdge(
        SemanticModel model, TypeOfExpressionSyntax typeofExpr,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var typeInfo = model.GetTypeInfo(typeofExpr.Type);
        var referencedType = typeInfo.Type as INamedTypeSymbol;
        if (referencedType == null) return;
        if (!IsFromSource(referencedType)) return;

        var containingType = FindContainingType(model, typeofExpr);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(referencedType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Extract References edges for attribute types.
    /// [Obsolete] references System.ObsoleteAttribute (filtered by IsFromSource).
    /// [MyCustomAttribute] references the source-defined attribute class.
    /// </summary>
    private void ExtractAttributeEdge(
        SemanticModel model, AttributeSyntax attribute,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var attrInfo = model.GetSymbolInfo(attribute);
        var attrCtor = attrInfo.Symbol as IMethodSymbol;
        if (attrCtor?.ContainingType == null) return;
        if (!IsFromSource(attrCtor.ContainingType)) return;

        var containingType = FindContainingType(model, attribute);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(attrCtor.ContainingType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Find the containing method or constructor for a syntax node.
    /// Local functions and property accessors are skipped — they don't have matching
    /// symbols in the graph, so edges from them would be dangling. We attribute
    /// their calls to the enclosing method instead.
    /// </summary>
    private static IMethodSymbol? FindContainingMethodOrLocal(SemanticModel model, SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax:
                    // Local functions aren't extracted as graph symbols — attributing edges
                    // to them creates dangling sources. Skip to the enclosing method.
                    continue;
                case MethodDeclarationSyntax method:
                    return model.GetDeclaredSymbol(method);
                case ConstructorDeclarationSyntax ctor:
                    return model.GetDeclaredSymbol(ctor);
                case AccessorDeclarationSyntax:
                    // Property accessors (get_X/set_X) are compiler-generated methods with no
                    // matching symbol in the graph. Skip to the containing method/type instead
                    // to avoid dangling edge sources.
                    continue;
            }
        }
        return null;
    }

    private static INamedTypeSymbol? FindContainingType(SemanticModel model, SyntaxNode node)
    {
        var typeNode = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeNode != null)
            return model.GetDeclaredSymbol(typeNode);

        var enumNode = node.Ancestors().OfType<EnumDeclarationSyntax>().FirstOrDefault();
        if (enumNode != null)
            return model.GetDeclaredSymbol(enumNode);

        return null;
    }

    private static string GetMethodId(IMethodSymbol method)
    {
        var paramSig = string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()));
        return SymbolIds.Method(
            RoslynSymbolExtractor.GetFullName(method.ContainingType),
            method.Name, paramSig);
    }

    /// <summary>
    /// Returns true if the symbol is defined in source (not from a metadata/BCL reference).
    /// Only creates edges to source-defined symbols to avoid dangling targets.
    /// Filters out System.* types that appear source-like due to compiler-generated code.
    /// </summary>
    private static bool IsFromSource(ISymbol? symbol)
    {
        if (symbol == null) return false;
        if (symbol.DeclaringSyntaxReferences.Length == 0) return false;
        // Filter BCL types — check for "System" as a complete namespace segment.
        // Must not filter user namespaces like "SystemManager" or "SystemConfig".
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)) return false;
        // Microsoft.* is also BCL/framework
        if (ns.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;
        return true;
    }

    private static void AddEdge(
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen,
        string sourceId, string targetId, EdgeKind kind)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) return;
        // Guard against prefix-only IDs (e.g., "type:" with no name)
        if (sourceId.EndsWith(':') || targetId.EndsWith(':')) return;
        if (!seen.Add((sourceId, targetId, kind))) return;

        edges.Add(new Edge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = kind,
            Evidence = SemanticEvidence,
        });
    }
}
