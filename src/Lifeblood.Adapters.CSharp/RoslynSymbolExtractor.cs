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
        // containing-type ParentId. Pre-fix, DescendantNodes() visited every
        // declaration regardless of nesting, so a nested type was extracted
        // twice — once with parentId=file (wrong) and once with parentId=type
        // (correct). GraphBuilder's first-write-wins dedup hid the duplicate
        // for plain types, but enum-member extraction (INV-EXTRACT-ENUMMEMBER-001)
        // produces members at the time of the type-level visit, so dups would
        // surface as duplicate enum-member emission.
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

        var typeId = SymbolIds.Type(GetFullName(typeSymbol));
        var containerId = typeSymbol.ContainingType != null
            ? SymbolIds.Type(GetFullName(typeSymbol.ContainingType))
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
            QualifiedName = GetFullName(typeSymbol),
            Kind = DomainSymbolKind.Type,
            FilePath = filePath,
            Line = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ParentId = containerId,
            Visibility = MapVisibility(typeSymbol.DeclaredAccessibility),
            IsAbstract = typeSymbol.IsAbstract,
            IsStatic = typeSymbol.IsStatic,
            Properties = typeProps,
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

        var enumFqn = GetFullName(enumSymbol);
        var enumId = SymbolIds.Type(enumFqn);

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
        // symbol. Pre-fix, ExtractEnum stopped at the type and enum members
        // (Color.Red etc.) never entered the graph. Three failure modes followed:
        //   (1) Exact-ID lookup field:NS.Color.Red missed → resolver Rule 4
        //       silently substituted any short-name 'Red' on a different type.
        //       See INV-RESOLVER-007 + R2-3 in IMPROVEMENT_INBOX.
        //   (2) References to enum members were dropped by GraphBuilder's
        //       dangling-edge filter (line 89), so find_references / dependants
        //       returned 0 hits for valid usages.
        //   (3) Dead-code analysis could never observe enum-member usage —
        //       every member appeared unreferenced in principle.
        // Roslyn models enum members as IFieldSymbol, so RoslynEdgeExtractor's
        // existing IFieldSymbol arm at line 391 already emits References edges
        // to the field-shape ID we synthesize here — no edge-extractor change
        // needed; the symbols just have to exist.
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
                Id = SymbolIds.Field(enumFqn, memberSym.Name),
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
        var paramSig = CanonicalSymbolFormat.BuildParamSignature(sym);
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
        var paramSig = CanonicalSymbolFormat.BuildParamSignature(sym);
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
                Id = SymbolIds.Field(typeName, sym.Name),
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
        var paramSig = CanonicalSymbolFormat.BuildIndexerParamSignature(sym);
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

    private void ExtractOperator(
        SemanticModel model, OperatorDeclarationSyntax operatorDecl,
        string containingTypeId, string filePath, List<Symbol> symbols)
    {
        var sym = model.GetDeclaredSymbol(operatorDecl) as IMethodSymbol;
        if (sym == null) return;

        var typeName = ExtractTypeFromId(containingTypeId);
        var paramSig = CanonicalSymbolFormat.BuildParamSignature(sym);
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(typeName, sym.Name, paramSig),
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
        var paramSig = CanonicalSymbolFormat.BuildParamSignature(sym);
        symbols.Add(new Symbol
        {
            Id = SymbolIds.Method(typeName, sym.Name, paramSig),
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
            Id = SymbolIds.Method(typeName, "Finalize", ""),
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
        var fqn = GetFullName(b);
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
