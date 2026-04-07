using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Extracts Lifeblood Symbol nodes from Roslyn's semantic model.
/// Types, methods, fields, properties → universal graph symbols.
/// </summary>
public sealed class RoslynSymbolExtractor
{
    public List<Symbol> Extract(SemanticModel model, SyntaxNode root, string relativeFilePath, string fileSymbolId)
    {
        var symbols = new List<Symbol>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax typeDecl:
                    ExtractType(model, typeDecl, relativeFilePath, fileSymbolId, symbols);
                    break;

                case DelegateDeclarationSyntax delegateDecl:
                    ExtractDelegate(model, delegateDecl, relativeFilePath, fileSymbolId, symbols);
                    break;
            }
        }

        return symbols;
    }

    private void ExtractType(
        SemanticModel model, BaseTypeDeclarationSyntax typeDecl,
        string filePath, string fileSymbolId, List<Symbol> symbols)
    {
        var typeSymbol = model.GetDeclaredSymbol(typeDecl);
        if (typeSymbol == null) return;

        var typeId = SymbolIds.Type(GetFullName(typeSymbol));
        var parentId = typeSymbol.ContainingType != null
            ? SymbolIds.Type(GetFullName(typeSymbol.ContainingType))
            : fileSymbolId;

        symbols.Add(new Symbol
        {
            Id = typeId,
            Name = typeSymbol.Name,
            QualifiedName = GetFullName(typeSymbol),
            Kind = SymbolKind.Type,
            FilePath = filePath,
            Line = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = parentId,
            Visibility = MapVisibility(typeSymbol.DeclaredAccessibility),
            IsAbstract = typeSymbol.IsAbstract,
            IsStatic = typeSymbol.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["typeKind"] = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
            },
        });

        // Extract members
        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax methodDecl:
                    ExtractMethod(model, methodDecl, typeId, filePath, symbols);
                    break;

                case ConstructorDeclarationSyntax ctorDecl:
                    ExtractConstructor(model, ctorDecl, typeId, filePath, symbols);
                    break;

                case FieldDeclarationSyntax fieldDecl:
                    ExtractField(model, fieldDecl, typeId, filePath, symbols);
                    break;

                case PropertyDeclarationSyntax propDecl:
                    ExtractProperty(model, propDecl, typeId, filePath, symbols);
                    break;

                case BaseTypeDeclarationSyntax nestedType:
                    ExtractType(model, nestedType, filePath, typeId, symbols);
                    break;
            }
        }
    }

    private void ExtractDelegate(
        SemanticModel model, DelegateDeclarationSyntax delegateDecl,
        string filePath, string fileSymbolId, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(delegateDecl);
        if (sym == null) return;

        symbols.Add(new Symbol
        {
            Id = SymbolIds.Type(GetFullName(sym)),
            Name = sym.Name,
            QualifiedName = GetFullName(sym),
            Kind = SymbolKind.Type,
            FilePath = filePath,
            Line = delegateDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = fileSymbolId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            Properties = new Dictionary<string, string> { ["typeKind"] = "delegate" },
        });
    }

    private void ExtractMethod(
        SemanticModel model, MethodDeclarationSyntax methodDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(methodDecl);
        if (sym == null) return;

        var paramSig = string.Join(",", sym.Parameters.Select(p => p.Type.ToDisplayString()));
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(ExtractTypeFromId(containingTypeId), sym.Name, paramSig),
            Name = sym.Name,
            QualifiedName = $"{ExtractTypeFromId(containingTypeId)}.{sym.Name}",
            Kind = SymbolKind.Method,
            FilePath = filePath,
            Line = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            IsStatic = sym.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["returnType"] = sym.ReturnType.ToDisplayString(),
                ["paramCount"] = sym.Parameters.Length.ToString(),
            },
        });
    }

    private void ExtractConstructor(
        SemanticModel model, ConstructorDeclarationSyntax ctorDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(ctorDecl);
        if (sym == null) return;

        var paramSig = string.Join(",", sym.Parameters.Select(p => p.Type.ToDisplayString()));
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(ExtractTypeFromId(containingTypeId), ".ctor", paramSig),
            Name = ".ctor",
            QualifiedName = $"{ExtractTypeFromId(containingTypeId)}..ctor",
            Kind = SymbolKind.Method,
            FilePath = filePath,
            Line = ctorDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsStatic = sym.IsStatic,
        });
    }

    private void ExtractField(
        SemanticModel model, FieldDeclarationSyntax fieldDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            var sym = model.GetDeclaredSymbol(variable) as IFieldSymbol;
            if (sym == null) continue;

            symbols.Add(new Symbol
            {
                Id = SymbolIds.Field(ExtractTypeFromId(containingTypeId), sym.Name),
                Name = sym.Name,
                QualifiedName = $"{ExtractTypeFromId(containingTypeId)}.{sym.Name}",
                Kind = SymbolKind.Field,
                FilePath = filePath,
                Line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ParentId = containingTypeId,
                Visibility = MapVisibility(sym.DeclaredAccessibility),
                IsStatic = sym.IsStatic,
                Properties = new Dictionary<string, string>
                {
                    ["fieldType"] = sym.Type.ToDisplayString(),
                },
            });
        }
    }

    private void ExtractProperty(
        SemanticModel model, PropertyDeclarationSyntax propDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(propDecl);
        if (sym == null) return;

        symbols.Add(new Symbol
        {
            Id = SymbolIds.Property(ExtractTypeFromId(containingTypeId), sym.Name),
            Name = sym.Name,
            QualifiedName = $"{ExtractTypeFromId(containingTypeId)}.{sym.Name}",
            Kind = SymbolKind.Field, // Properties map to Field in the universal model
            FilePath = filePath,
            Line = propDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            IsStatic = sym.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["propertyType"] = sym.Type.ToDisplayString(),
                ["isProperty"] = "true",
            },
        });
    }

    internal static string GetFullName(ISymbol symbol)
    {
        var parts = new List<string>();
        var current = symbol;
        while (current != null && current is not INamespaceSymbol { IsGlobalNamespace: true })
        {
            if (current is INamespaceSymbol ns && !string.IsNullOrEmpty(ns.Name))
                parts.Add(ns.Name);
            else if (current is INamedTypeSymbol || current is IMethodSymbol || current is IFieldSymbol || current is IPropertySymbol)
                parts.Add(current.Name);

            current = current.ContainingSymbol;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string ExtractTypeFromId(string typeId)
        => typeId.StartsWith("type:") ? typeId.Substring(5) : typeId;

    private static Visibility MapVisibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => Visibility.Public,
        Accessibility.Internal => Visibility.Internal,
        Accessibility.Protected => Visibility.Protected,
        Accessibility.ProtectedOrInternal => Visibility.Protected,
        Accessibility.ProtectedAndInternal => Visibility.Internal,
        Accessibility.Private => Visibility.Private,
        _ => Visibility.Internal,
    };
}
