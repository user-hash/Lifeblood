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

        // Outer scan handles TOP-LEVEL declarations only (those parented to a
        // compilation unit or a namespace). Nested types/enums/delegates are
        // discovered via ExtractType's member walker, which threads the correct
        // containing-type ParentId. INV-EXTRACT-ENUMMEMBER-001 requires each
        // type-level visit to emit enum members exactly once, so the outer
        // scan must not double-visit nested type declarations.
        foreach (var node in root.DescendantNodes())
        {
            if (!IsTopLevelDeclaration(node)) continue;

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

    private static bool IsTopLevelDeclaration(SyntaxNode node)
        => node.Parent is CompilationUnitSyntax
            or NamespaceDeclarationSyntax
            or FileScopedNamespaceDeclarationSyntax;

    private void ExtractType(
        SemanticModel model, TypeDeclarationSyntax typeDecl,
        string filePath, string parentId, List<Symbol> symbols)
    {
        var typeSymbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (typeSymbol == null) return;

        var typeId = CanonicalSymbolFormat.BuildTypeId(typeSymbol);
        var containerId = typeSymbol.ContainingType != null
            ? CanonicalSymbolFormat.BuildTypeId(typeSymbol.ContainingType)
            : parentId;

        var typeProps = new Dictionary<string, string>
        {
            ["typeKind"] = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
        };
        AttachBaseTypeFqn(typeProps, typeSymbol);
        AttachAttributeNames(typeProps, typeSymbol);
        AttachXmlDocSummary(typeProps, typeSymbol);
        symbols.Add(new Symbol
        {
            Id = typeId,
            Name = typeSymbol.Name,
            QualifiedName = CanonicalSymbolFormat.GetFullName(typeSymbol),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containerId,
            Visibility = MapVisibility(typeSymbol.DeclaredAccessibility),
            IsAbstract = typeSymbol.IsAbstract,
            IsStatic = typeSymbol.IsStatic,
            Properties = typeProps,
        });

        // Surface synthesized .cctor / parameterless .ctor when the type
        // carries field or property initializers. RoslynEdgeExtractor
        // attributes initializer-derived edges (method-group references,
        // member-access references, etc.) to the synthesized constructor
        // returned by `ContainingType.StaticConstructors.FirstOrDefault()`
        // / `InstanceConstructors.FirstOrDefault()`. GraphBuilder drops
        // any edge whose source is not also a Symbol, so without these
        // surfaces every dispatch-table delegate target and every static-
        // initializer-referenced enum/field would silently vanish from
        // the graph — exactly the LB-INBOX-011 part 2 false-positive
        // class where delegate row methods were reported as dead
        // despite live use via dispatch tables. The surfaced symbols
        // carry the same canonical id the edge attribution
        // uses (`method:NS.T..cctor()` or `method:NS.T..ctor()`) so
        // GraphBuilder's edge filter retains them.
        // INV-EXTRACT-SYNTHESIZED-CTOR-001.
        SurfaceSynthesizedInitializerConstructors(typeSymbol, typeId, filePath, symbols);

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

                case OperatorDeclarationSyntax operatorDecl:
                    ExtractOperator(model, operatorDecl, typeId, filePath, symbols);
                    break;

                case ConversionOperatorDeclarationSyntax conversionDecl:
                    ExtractConversionOperator(model, conversionDecl, typeId, filePath, symbols);
                    break;

                case DestructorDeclarationSyntax destructorDecl:
                    ExtractDestructor(model, destructorDecl, typeId, filePath, symbols);
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

        var enumFqn = CanonicalSymbolFormat.GetFullName(enumSymbol);
        var enumId = CanonicalSymbolFormat.BuildTypeId(enumSymbol);

        symbols.Add(new Symbol
        {
            Id = enumId,
            Name = enumSymbol.Name,
            QualifiedName = enumFqn,
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = enumDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = parentId,
            Visibility = MapVisibility(enumSymbol.DeclaredAccessibility),
            Properties = new Dictionary<string, string> { ["typeKind"] = "enum" },
        });

        // INV-EXTRACT-ENUMMEMBER-001: every enum member is a first-class graph
        // symbol. Roslyn models enum members as IFieldSymbol; RoslynEdgeExtractor's
        // IFieldSymbol arm emits References edges to the field-shape ID we
        // synthesize here — no edge-extractor change needed, the symbols just
        // have to exist. INV-RESOLVER-007 + R2-3 reachability hinge on this.
        foreach (var memberDecl in enumDecl.Members)
        {
            var memberSym = model.GetDeclaredSymbol(memberDecl) as IFieldSymbol;
            if (memberSym == null) continue;

            var memberProps = new Dictionary<string, string>
            {
                ["fieldKind"] = "enumMember",
                ["fieldType"] = enumFqn,
            };
            var constantValue = memberSym.ConstantValue;
            if (constantValue != null)
                memberProps["constantValue"] = constantValue.ToString() ?? "";
            AttachXmlDocSummary(memberProps, memberSym);

            symbols.Add(new Symbol
            {
                Id = CanonicalSymbolFormat.BuildFieldId(memberSym),
                Name = memberSym.Name,
                QualifiedName = $"{enumFqn}.{memberSym.Name}",
                Kind = DomainSymbolKind.Field,
                FilePath = filePath,
                Line = memberDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ParentId = enumId,
                Visibility = MapVisibility(enumSymbol.DeclaredAccessibility),
                IsStatic = true,
                Properties = memberProps,
            });
        }
    }

    private void ExtractDelegate(
        SemanticModel model, DelegateDeclarationSyntax delegateDecl,
        string filePath, string parentId, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(delegateDecl) as INamedTypeSymbol;
        if (sym == null) return;

        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildTypeId(sym),
            Name = sym.Name,
            QualifiedName = CanonicalSymbolFormat.GetFullName(sym),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = delegateDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = parentId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            Properties = new Dictionary<string, string> { ["typeKind"] = "delegate" },
        });
    }

    /// <summary>
    /// Emit Symbol records for synthesized `.cctor` and parameterless
    /// `.ctor()` when the type's initializers would route graph edges
    /// through them. The static and instance cases are independent:
    /// a type that mixes a user-declared parameterized ctor with one
    /// or more static field initializers still synthesizes `.cctor`
    /// even though `InstanceConstructors[0]` belongs to the user.
    /// Likewise a type with only instance initializers but a
    /// user-declared parameterized ctor still synthesizes a default
    /// parameterless `.ctor()` only if no user-declared `.ctor()`
    /// exists — Roslyn surfaces the synthesizer's emit choices on
    /// `INamedTypeSymbol.{StaticConstructors,InstanceConstructors}`
    /// with empty `DeclaringSyntaxReferences`.
    /// </summary>
    private void SurfaceSynthesizedInitializerConstructors(
        INamedTypeSymbol typeSymbol, string typeId, string filePath, List<Symbol> symbols)
    {
        bool hasStaticInitializer = false;
        bool hasInstanceInitializer = false;
        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when MemberHasInitializerSyntax(field):
                    if (field.IsStatic) hasStaticInitializer = true;
                    else hasInstanceInitializer = true;
                    break;
                case IPropertySymbol prop when MemberHasInitializerSyntax(prop):
                    if (prop.IsStatic) hasStaticInitializer = true;
                    else hasInstanceInitializer = true;
                    break;
            }
            if (hasStaticInitializer && hasInstanceInitializer) break;
        }

        var typeName = ExtractTypeFromId(typeId);

        if (hasStaticInitializer)
        {
            var cctor = typeSymbol.StaticConstructors.FirstOrDefault(c => c.DeclaringSyntaxReferences.IsEmpty);
            if (cctor != null)
                symbols.Add(BuildSynthesizedConstructorSymbol(cctor, typeName, typeId, filePath, isStatic: true));
        }

        if (hasInstanceInitializer)
        {
            var defaultCtor = typeSymbol.InstanceConstructors
                .FirstOrDefault(c => c.Parameters.Length == 0 && c.DeclaringSyntaxReferences.IsEmpty);
            if (defaultCtor != null)
                symbols.Add(BuildSynthesizedConstructorSymbol(defaultCtor, typeName, typeId, filePath, isStatic: false));
        }
    }

    private static bool MemberHasInitializerSyntax(IFieldSymbol field)
    {
        foreach (var syntaxRef in field.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is VariableDeclaratorSyntax v && v.Initializer != null)
                return true;
        }
        return false;
    }

    private static bool MemberHasInitializerSyntax(IPropertySymbol prop)
    {
        foreach (var syntaxRef in prop.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is PropertyDeclarationSyntax p && p.Initializer != null)
                return true;
        }
        return false;
    }

    private static Symbol BuildSynthesizedConstructorSymbol(
        IMethodSymbol ctor, string typeName, string typeId, string filePath, bool isStatic)
    {
        var name = isStatic ? ".cctor" : ".ctor";
        return new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(ctor),
            Name = name,
            QualifiedName = $"{typeName}.{name}",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = 0,
            ParentId = typeId,
            Visibility = MapVisibility(ctor.DeclaredAccessibility),
            IsStatic = isStatic,
            Properties = new Dictionary<string, string>
            {
                ["paramCount"] = "0",
                ["synthesized"] = "true",
            },
        };
    }

    private void ExtractMethod(
        SemanticModel model, MethodDeclarationSyntax methodDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var methodProps = new Dictionary<string, string>
        {
            ["returnType"] = sym.ReturnType.ToDisplayString(),
            ["paramCount"] = sym.Parameters.Length.ToString(),
        };
        AttachAttributeNames(methodProps, sym);
        AttachMethodClassification(methodProps, methodDecl);
        AttachXmlDocSummary(methodProps, sym);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(sym),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            IsStatic = sym.IsStatic,
            Properties = methodProps,
        });
    }

    private void ExtractConstructor(
        SemanticModel model, ConstructorDeclarationSyntax ctorDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(ctorDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var name = sym.IsStatic ? ".cctor" : ".ctor";
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(sym),
            Name = name,
            QualifiedName = $"{typeName}.{name}",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = ctorDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsStatic = sym.IsStatic,
            Properties = new Dictionary<string, string>
            {
                ["paramCount"] = sym.Parameters.Length.ToString(),
            },
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
            var fieldProps = new Dictionary<string, string>
            {
                ["fieldType"] = sym.Type.ToDisplayString(),
            };
            AttachXmlDocSummary(fieldProps, sym);
            symbols.Add(new Symbol
            {
                Id = CanonicalSymbolFormat.BuildFieldId(sym),
                Name = sym.Name,
                QualifiedName = $"{typeName}.{sym.Name}",
                Kind = DomainSymbolKind.Field,
                FilePath = filePath,
                Line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ParentId = containingTypeId,
                Visibility = MapVisibility(sym.DeclaredAccessibility),
                IsStatic = sym.IsStatic,
                Properties = fieldProps,
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
        var propProps = new Dictionary<string, string>
        {
            ["propertyType"] = sym.Type.ToDisplayString(),
            ["isProperty"] = "true",
        };
        AttachXmlDocSummary(propProps, sym);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildPropertyId(sym),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Property,
            FilePath = filePath,
            Line = propDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsAbstract = sym.IsAbstract,
            IsStatic = sym.IsStatic,
            Properties = propProps,
        });
    }

    private void ExtractIndexer(
        SemanticModel model, IndexerDeclarationSyntax indexerDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(indexerDecl) as IPropertySymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildPropertyId(sym),
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

    private void ExtractOperator(
        SemanticModel model, OperatorDeclarationSyntax operatorDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(operatorDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(sym),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = operatorDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsStatic = true,
            Properties = new Dictionary<string, string>
            {
                ["returnType"] = sym.ReturnType.ToDisplayString(),
                ["isOperator"] = "true",
            },
        });
    }

    private void ExtractConversionOperator(
        SemanticModel model, ConversionOperatorDeclarationSyntax conversionDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(conversionDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(sym),
            Name = sym.Name,
            QualifiedName = $"{typeName}.{sym.Name}",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = conversionDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = MapVisibility(sym.DeclaredAccessibility),
            IsStatic = true,
            Properties = new Dictionary<string, string>
            {
                ["returnType"] = sym.ReturnType.ToDisplayString(),
                ["isOperator"] = "true",
            },
        });
    }

    private void ExtractDestructor(
        SemanticModel model, DestructorDeclarationSyntax destructorDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(destructorDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        symbols.Add(new Symbol
        {
            Id = CanonicalSymbolFormat.BuildMethodId(sym),
            Name = "Finalize",
            QualifiedName = $"{typeName}.Finalize",
            Kind = DomainSymbolKind.Method,
            FilePath = filePath,
            Line = destructorDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containingTypeId,
            Visibility = Visibility.Protected,
            Properties = new Dictionary<string, string>
            {
                ["paramCount"] = "0",
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
            Id = CanonicalSymbolFormat.BuildEventId(sym),
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

    /// <summary>
    /// Attach the symbol's <c>&lt;summary&gt;</c> XML documentation to the
    /// <see cref="Symbol.Properties"/> dictionary under the
    /// <c>xmlDocSummary</c> key. Called during full and incremental
    /// extraction so the <c>lifeblood_search</c> tool can search by
    /// docstring text without needing a live Roslyn compilation.
    ///
    /// Only persists when the documentation is non-empty — an empty value
    /// would bloat the serialized graph for every undocumented symbol.
    /// </summary>
    private static void AttachXmlDocSummary(IDictionary<string, string> props, ISymbol sym)
    {
        var summary = Internal.XmlDocExtractor.ExtractSummary(sym);
        if (!string.IsNullOrEmpty(summary))
            props["xmlDocSummary"] = summary;
    }

    /// <summary>
    /// Record the qualified name of the type's direct base on its
    /// <c>Properties["baseType"]</c>. Lets downstream analysis walk the
    /// inheritance chain even into types that live outside the loaded
    /// workspace (UnityEngine.MonoBehaviour, System.Attribute, ASP.NET
    /// ControllerBase, etc.) where the C# adapter would otherwise drop
    /// the Inherits edge as dangling. Skips System.Object /
    /// System.ValueType / System.Enum / System.Delegate because every
    /// type ultimately derives from one of those and recording it would
    /// just be noise.
    /// </summary>
    private static void AttachBaseTypeFqn(IDictionary<string, string> props, INamedTypeSymbol typeSymbol)
    {
        var b = typeSymbol.BaseType;
        if (b == null) return;
        switch (b.SpecialType)
        {
            case SpecialType.System_Object:
            case SpecialType.System_ValueType:
            case SpecialType.System_Enum:
            case SpecialType.System_Delegate:
            case SpecialType.System_MulticastDelegate:
                return;
        }
        var fqn = CanonicalSymbolFormat.GetFullName(b);
        if (!string.IsNullOrEmpty(fqn))
            props["baseType"] = fqn;
    }

    /// <summary>
    /// Classify the method body by shape so authority / forwarder
    /// analyzers can read the verdict directly off
    /// <c>Properties["classification"]</c> without re-walking syntax:
    /// <list type="bullet">
    ///   <item><b>PureForwarder</b> — single-statement or expression-bodied
    ///     method whose body is exactly one invocation on a member
    ///     access (<c>x.Foo()</c>, <c>_field.Bar()</c>, <c>this.Baz()</c>).</item>
    ///   <item><b>ThinWrapper</b> — body has at most 5 statements and
    ///     contains exactly one invocation expression. Captures
    ///     null-guard-then-delegate, simple cast-then-delegate, etc.</item>
    ///   <item><b>RealLogic</b> — anything else (multi-call, control
    ///     flow, loops, complex expressions).</item>
    /// </list>
    /// Abstract / partial / extern bodies and methods with no body are
    /// not classified (no entry written) — there's no body shape to
    /// classify and the authority report explicitly handles missing
    /// classification with a sentinel ratio. INV-FORWARDER-001.
    /// </summary>
    private static void AttachMethodClassification(
        IDictionary<string, string> props,
        MethodDeclarationSyntax methodDecl)
    {
        // Expression-bodied: `=> body` — one expression. If it's an
        // invocation it's a PureForwarder; otherwise a ThinWrapper if
        // it contains exactly one invocation, else RealLogic.
        if (methodDecl.ExpressionBody != null)
        {
            var expr = methodDecl.ExpressionBody.Expression;
            if (expr is InvocationExpressionSyntax)
            {
                props["classification"] = "PureForwarder";
                return;
            }
            int invCount = expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Count();
            props["classification"] = invCount == 1 ? "ThinWrapper" : "RealLogic";
            return;
        }

        var body = methodDecl.Body;
        if (body == null) return; // abstract / partial / extern

        var stmts = body.Statements;
        if (stmts.Count == 0)
        {
            props["classification"] = "RealLogic"; // empty {}; treat as not a forwarder
            return;
        }

        // Single statement: ExpressionStatementSyntax wrapping an
        // invocation, or ReturnStatementSyntax wrapping an invocation.
        if (stmts.Count == 1)
        {
            var only = stmts[0];
            if (only is ExpressionStatementSyntax es && es.Expression is InvocationExpressionSyntax)
            {
                props["classification"] = "PureForwarder";
                return;
            }
            if (only is ReturnStatementSyntax rs && rs.Expression is InvocationExpressionSyntax)
            {
                props["classification"] = "PureForwarder";
                return;
            }
        }

        // ThinWrapper: <= 5 statements, exactly one invocation overall.
        if (stmts.Count <= 5)
        {
            int invCount = body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Count();
            if (invCount == 1)
            {
                props["classification"] = "ThinWrapper";
                return;
            }
        }

        props["classification"] = "RealLogic";
    }

    /// <summary>
    /// Record the simple attribute class names on the symbol (semicolon-
    /// separated) so downstream analysis can detect framework dispatch
    /// without re-running Roslyn — Unity reachability,
    /// SerializedField inference, custom-editor classification, etc.
    /// Stores only the simple name (e.g. "RuntimeInitializeOnLoadMethod"),
    /// not the namespace, because attribute names are unique enough at
    /// the class-name level for the dispatch-detection use case and the
    /// payload stays compact for very-large workspaces.
    /// </summary>
    private static void AttachAttributeNames(IDictionary<string, string> props, ISymbol sym)
    {
        var attrs = sym.GetAttributes();
        if (attrs.Length == 0) return;
        var names = new List<string>(attrs.Length);
        foreach (var a in attrs)
        {
            var n = a.AttributeClass?.Name;
            if (string.IsNullOrEmpty(n)) continue;
            // Roslyn returns "FooAttribute" for [Foo]; strip the suffix
            // so consumers can match the user-facing form. "Attribute"
            // alone (the bare class) keeps its name.
            if (n.Length > 9 && n.EndsWith("Attribute", StringComparison.Ordinal))
                n = n.Substring(0, n.Length - 9);
            names.Add(n);
        }
        if (names.Count == 0) return;
        props[SymbolPropertyKeys.Attributes] = string.Join(";", names);
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
