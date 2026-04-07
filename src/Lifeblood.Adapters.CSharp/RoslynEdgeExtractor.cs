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

        var caller = FindContainingMethod(model, invocation);
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

        var caller = FindContainingMethod(model, creation);
        if (caller == null) return;

        var sourceId = GetMethodId(caller);
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));
        AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    private void ExtractReferenceEdge(
        SemanticModel model, IdentifierNameSyntax identifier,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        // Skip identifiers that are part of declarations
        if (identifier.Parent is BaseTypeDeclarationSyntax) return;
        if (identifier.Parent is MethodDeclarationSyntax) return;

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

    private static IMethodSymbol? FindContainingMethod(SemanticModel model, SyntaxNode node)
    {
        // Try MethodDeclarationSyntax first, then ConstructorDeclarationSyntax
        var methodNode = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodNode != null)
            return model.GetDeclaredSymbol(methodNode);

        var ctorNode = node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctorNode != null)
            return model.GetDeclaredSymbol(ctorNode);

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
        // ValueTuple is compiler-generated — treat as external
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns.StartsWith("System", StringComparison.Ordinal)) return false;
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
