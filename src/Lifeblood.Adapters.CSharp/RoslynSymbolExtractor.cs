using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

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
                case TypeDeclarationSyntax typeDecl:
                    ExtractType(model, typeDecl, relativeFilePath, fileSymbolId, symbols);
                    break;

                case EnumDeclarationSyntax enumDecl:
                    ExtractEnum(model, enumDecl, relativeFilePath, fileSymbolId, symbols);
                    break;

                case DelegateDeclarationSyntax delegateDecl:
                    ExtractDelegate(model, delegateDecl, relativeFilePath, fileSymbolId, symbols);
                    break;
            }
        }

        return symbols;
    }

    private void ExtractType(
        SemanticModel model, TypeDeclarationSyntax typeDecl,
        string filePath, string parentId, List<Symbol> symbols)
    {
        var typeSymbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (typeSymbol == null) return;

        var typeId = SymbolIds.Type(GetFullName(typeSymbol));
        var containerId = typeSymbol.ContainingType != null
            ? SymbolIds.Type(GetFullName(typeSymbol.ContainingType))
            : parentId;

        symbols.Add(new Symbol
        {
            Id = typeId,
            Name = typeSymbol.Name,
            QualifiedName = GetFullName(typeSymbol),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containerId,
            Visibility = MapVisibility(typeSymbol.DeclaredAccessibility),
            IsAbstract = typeSymbol.IsAbstract,
            IsStatic = typeSymbol.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["typeKind"] = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
            },
        });

        // Extract members from TypeDeclarationSyntax (has .Members)
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

                case IndexerDeclarationSyntax indexerDecl:
                    ExtractIndexer(model, indexerDecl, typeId, filePath, symbols);
                    break;

                case EventDeclarationSyntax eventDecl:
                    ExtractEvent(model, eventDecl, typeId, filePath, symbols);
                    break;

                case EventFieldDeclarationSyntax eventFieldDecl:
                    ExtractEventField(model, eventFieldDecl, typeId, filePath, symbols);
                    break;

                case TypeDeclarationSyntax nestedType:
                    ExtractType(model, nestedType, filePath, typeId, symbols);
                    break;

                case EnumDeclarationSyntax nestedEnum:
                    ExtractEnum(model, nestedEnum, filePath, typeId, symbols);
                    break;
            }
        }
    }

    private void ExtractEnum(
        SemanticModel model, EnumDeclarationSyntax enumDecl,
        string filePath, string parentId, List<Symbol> symbols)
    {
        var enumSymbol = model.GetDeclaredSymbol(enumDecl) as INamedTypeSymbol;
        if (enumSymbol == null) return;

        symbols.Add(new Symbol
        {
            Id = SymbolIds.Type(GetFullName(enumSymbol)),
            Name = enumSymbol.Name,
            QualifiedName = GetFullName(enumSymbol),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = enumDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = parentId,
            Visibility = MapVisibility(enumSymbol.DeclaredAccessibility),
            Properties = new Dictionary<string, string> { ["typeKind"] = "enum" },
        });
    }

    private void ExtractDelegate(
        SemanticModel model, DelegateDeclarationSyntax delegateDecl,
        string filePath, string parentId, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(delegateDecl) as INamedTypeSymbol;
        if (sym == null) return;

        symbols.Add(new Symbol
        {
            Id = SymbolIds.Type(GetFullName(sym)),
            Name = sym.Name,
            QualifiedName = GetFullName(sym),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = delegateDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = parentId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            Properties = new Dictionary<string, string> { ["typeKind"] = "delegate" },
        });
    }

    private void ExtractMethod(
        SemanticModel model, MethodDeclarationSyntax methodDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var paramSig = string.Join(",", sym.Parameters.Select(p => p.Type.ToDisplayString()));
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(typeName, sym.Name, paramSig),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Method,
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
        var sym = model.GetDeclaredSymbol(ctorDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var paramSig = string.Join(",", sym.Parameters.Select(p => p.Type.ToDisplayString()));
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(typeName, ".ctor", paramSig),
            Name = ".ctor",
            QualifiedName = $"{typeName}..ctor",
            Kind = DomainSymbolKind.Method,
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

            var typeName = ExtractTypeFromId(containingTypeId);
            symbols.Add(new Symbol
            {
                Id = SymbolIds.Field(typeName, sym.Name),
                Name = sym.Name,
                QualifiedName = $"{typeName}.{sym.Name}",
                Kind = DomainSymbolKind.Field,
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
        var sym = model.GetDeclaredSymbol(propDecl) as IPropertySymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Property(typeName, sym.Name),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Property,
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

    private void ExtractIndexer(
        SemanticModel model, IndexerDeclarationSyntax indexerDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(indexerDecl) as IPropertySymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var paramSig = string.Join(",", sym.Parameters.Select(p => p.Type.ToDisplayString()));
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Property(typeName, $"this[{paramSig}]"),
            Name = "this[]",
            QualifiedName = $"{typeName}.this[]",
            Kind = DomainSymbolKind.Property,
            FilePath = filePath,
            Line = indexerDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            Properties = new Dictionary<string, string>
            {
                ["propertyType"] = sym.Type.ToDisplayString(),
                ["isIndexer"] = "true",
            },
        });
    }

    /// <summary>
    /// Explicit event declaration: event EventHandler Changed { add { } remove { } }
    /// </summary>
    private void ExtractEvent(
        SemanticModel model, EventDeclarationSyntax eventDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(eventDecl) as IEventSymbol;
        if (sym == null) return;

        ExtractEventSymbol(sym, containingTypeId, filePath, eventDecl.GetLocation(), symbols);
    }

    /// <summary>
    /// Field-like event declaration: event EventHandler? Changed;
    /// One declaration can declare multiple events (rare but valid).
    /// </summary>
    private void ExtractEventField(
        SemanticModel model, EventFieldDeclarationSyntax eventFieldDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        foreach (var variable in eventFieldDecl.Declaration.Variables)
        {
            var sym = model.GetDeclaredSymbol(variable) as IEventSymbol;
            if (sym == null) continue;

            ExtractEventSymbol(sym, containingTypeId, filePath, variable.GetLocation(), symbols);
        }
    }

    private void ExtractEventSymbol(
        IEventSymbol sym, string containingTypeId, string filePath,
        Location location, List<Symbol> symbols)
    {
        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Property(typeName, sym.Name),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Property,
            FilePath = filePath,
            Line = location.GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            IsStatic = sym.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["eventType"] = sym.Type.ToDisplayString(),
                ["isEvent"] = "true",
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
            else if (current is INamedTypeSymbol or IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol)
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
