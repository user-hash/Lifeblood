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

    /// <summary>
    /// Assembly names of all modules in the workspace. When set, cross-module edges
    /// are extracted: metadata symbols whose ContainingAssembly matches a known module
    /// are treated as tracked (not filtered out like BCL/framework types).
    /// Set by RoslynWorkspaceAnalyzer before processing begins.
    /// </summary>
    public HashSet<string>? KnownModuleAssemblies { get; set; }

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

                case BaseObjectCreationExpressionSyntax creation:
                    ExtractConstructorCallEdge(model, creation, edges, seen);
                    break;

                case IdentifierNameSyntax identifier:
                    ExtractReferenceEdge(model, identifier, edges, seen);
                    break;

                case MemberAccessExpressionSyntax memberAccess:
                    ExtractMemberAccessEdge(model, memberAccess, edges, seen);
                    break;

                case MemberBindingExpressionSyntax memberBinding:
                    ExtractMemberBindingEdge(model, memberBinding, edges, seen);
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
            && IsTracked(typeSymbol.BaseType))
        {
            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(typeSymbol.BaseType));
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.Inherits);
        }

        // Interfaces → Implements (only source-defined interfaces)
        foreach (var iface in typeSymbol.Interfaces)
        {
            if (!IsTracked(iface)) continue;
            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(iface));
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.Implements);
        }

        // Member overrides → Overrides (virtual dispatch chain)
        // Walk all members to find overrides. Each override points to
        // the specific base member it overrides — not just the base type.
        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method when method.OverriddenMethod != null
                    && IsTracked(method.OverriddenMethod):
                {
                    AddEdge(edges, seen,
                        GetMethodId(method), GetMethodId(method.OverriddenMethod),
                        EdgeKind.Overrides);
                    break;
                }
                case IPropertySymbol prop when prop.OverriddenProperty != null
                    && IsTracked(prop.OverriddenProperty):
                {
                    var typeFqn = RoslynSymbolExtractor.GetFullName(prop.ContainingType);
                    var baseFqn = RoslynSymbolExtractor.GetFullName(prop.OverriddenProperty.ContainingType);
                    // Indexers use "this[paramSig]" in their symbol ID (matching ExtractIndexer),
                    // while regular properties use just the name.
                    var propSourceId = prop.IsIndexer
                        ? SymbolIds.Property(typeFqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(prop)}]")
                        : SymbolIds.Property(typeFqn, prop.Name);
                    var propTargetId = prop.OverriddenProperty.IsIndexer
                        ? SymbolIds.Property(baseFqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(prop.OverriddenProperty)}]")
                        : SymbolIds.Property(baseFqn, prop.OverriddenProperty.Name);
                    AddEdge(edges, seen, propSourceId, propTargetId, EdgeKind.Overrides);
                    break;
                }
                case IEventSymbol evt when evt.OverriddenEvent != null
                    && IsTracked(evt.OverriddenEvent):
                {
                    var typeFqn = RoslynSymbolExtractor.GetFullName(evt.ContainingType);
                    var baseFqn = RoslynSymbolExtractor.GetFullName(evt.OverriddenEvent.ContainingType);
                    AddEdge(edges, seen,
                        SymbolIds.Property(typeFqn, evt.Name),
                        SymbolIds.Property(baseFqn, evt.OverriddenEvent.Name),
                        EdgeKind.Overrides);
                    break;
                }
            }
        }

        // Interface member implementations → method-level Implements
        // The type-level Implements edge above captures type:Concrete → type:IFoo.
        // This loop captures method:Concrete.M() → method:IFoo.M() so the dead-code
        // analyzer sees concrete implementations as reachable through the interface.
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            if (!IsTracked(iface)) continue;
            foreach (var ifaceMember in iface.GetMembers())
            {
                var impl = typeSymbol.FindImplementationForInterfaceMember(ifaceMember);
                if (impl == null || !SymbolEqualityComparer.Default.Equals(impl.ContainingType, typeSymbol))
                    continue; // skip inherited implementations

                switch (ifaceMember)
                {
                    case IMethodSymbol ifaceMethod when impl is IMethodSymbol implMethod:
                    {
                        AddEdge(edges, seen,
                            GetMethodId(implMethod), GetMethodId(ifaceMethod),
                            EdgeKind.Implements);
                        break;
                    }
                    case IPropertySymbol ifaceProp when impl is IPropertySymbol implProp:
                    {
                        var implFqn = RoslynSymbolExtractor.GetFullName(implProp.ContainingType);
                        var ifaceFqn = RoslynSymbolExtractor.GetFullName(ifaceProp.ContainingType);
                        var implPropId = implProp.IsIndexer
                            ? SymbolIds.Property(implFqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(implProp)}]")
                            : SymbolIds.Property(implFqn, implProp.Name);
                        var ifacePropId = ifaceProp.IsIndexer
                            ? SymbolIds.Property(ifaceFqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(ifaceProp)}]")
                            : SymbolIds.Property(ifaceFqn, ifaceProp.Name);
                        AddEdge(edges, seen, implPropId, ifacePropId, EdgeKind.Implements);
                        break;
                    }
                    case IEventSymbol ifaceEvt when impl is IEventSymbol implEvt:
                    {
                        var implFqn = RoslynSymbolExtractor.GetFullName(implEvt.ContainingType);
                        var ifaceFqn = RoslynSymbolExtractor.GetFullName(ifaceEvt.ContainingType);
                        AddEdge(edges, seen,
                            SymbolIds.Property(implFqn, implEvt.Name),
                            SymbolIds.Property(ifaceFqn, ifaceEvt.Name),
                            EdgeKind.Implements);
                        break;
                    }
                }
            }
        }
    }

    private void ExtractCallEdge(
        SemanticModel model, InvocationExpressionSyntax invocation,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(invocation);
        var target = symbolInfo.Symbol as IMethodSymbol;
        if (target?.ContainingType == null) return;
        if (!IsTracked(target)) return;

        var caller = FindContainingMethodOrLocal(model, invocation);
        if (caller == null) return;

        var sourceId = GetMethodId(caller);
        var paramSig = CanonicalSymbolFormat.BuildParamSignature(target);
        var targetId = SymbolIds.Method(
            RoslynSymbolExtractor.GetFullName(target.ContainingType),
            target.Name, paramSig);

        AddEdge(edges, seen, sourceId, targetId, EdgeKind.Calls);
    }

    /// <summary>
    /// Handles both explicit new (ObjectCreationExpressionSyntax) and
    /// target-typed new() (ImplicitObjectCreationExpressionSyntax, C# 9).
    /// Both share the base type BaseObjectCreationExpressionSyntax.
    /// Emits two edges: a type-level References edge (module coupling signal) AND
    /// a method-level Calls edge to the .ctor method so find_references on the
    /// constructor finds its call sites and the dead-code analyzer sees invoked
    /// constructors as reachable.
    /// </summary>
    private void ExtractConstructorCallEdge(
        SemanticModel model, BaseObjectCreationExpressionSyntax creation,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(creation);
        var target = symbolInfo.Symbol as IMethodSymbol;
        if (target?.ContainingType == null) return;
        if (!IsTracked(target.ContainingType)) return;

        var caller = FindContainingMethodOrLocal(model, creation);
        if (caller == null) return;

        var sourceId = GetMethodId(caller);

        // Type-level edge: caller → containing type (module-coupling signal).
        var typeTargetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));
        AddEdge(edges, seen, sourceId, typeTargetId, EdgeKind.References);

        // Method-level edge: caller → .ctor. Only emit when the ctor itself is tracked
        // (source-declared or cross-module-known). Implicit default ctors produce metadata
        // symbols without DeclaringSyntaxReferences in the current compilation; IsTracked
        // accepts them when ContainingAssembly is a known workspace module.
        if (IsTracked(target))
        {
            var ctorTargetId = GetMethodId(target);
            AddEdge(edges, seen, sourceId, ctorTargetId, EdgeKind.Calls);
        }
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

        // Type references (existing behavior)
        if (referencedSymbol is INamedTypeSymbol referencedType)
        {
            if (!IsTracked(referencedType)) return;
            var containingType = FindContainingType(model, identifier);
            if (containingType == null) return;
            var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
            var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(referencedType));
            if (sourceId != targetId)
                AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
            return;
        }

        // Field references via bare identifier (e.g., _fs in _fs.ReadAllText())
        if (referencedSymbol is IFieldSymbol fieldSymbol
            && fieldSymbol.ContainingType != null
            && IsTracked(fieldSymbol.ContainingType))
        {
            var caller = FindContainingMethodOrLocal(model, identifier);
            if (caller == null) return;
            var callerMethodId = GetMethodId(caller);
            var fqn = RoslynSymbolExtractor.GetFullName(fieldSymbol.ContainingType);
            AddEdge(edges, seen, callerMethodId, SymbolIds.Field(fqn, fieldSymbol.Name), EdgeKind.References);
            return;
        }

        // Method-group references (new Lazy<T>(Load), event += Handler, Where(predicate))
        if (referencedSymbol is IMethodSymbol methodSymbol
            && methodSymbol.MethodKind != MethodKind.Constructor
            && methodSymbol.ContainingType != null
            && IsTracked(methodSymbol))
        {
            var caller = FindContainingMethodOrLocal(model, identifier);
            if (caller == null) return;
            AddEdge(edges, seen, GetMethodId(caller), GetMethodId(methodSymbol), EdgeKind.Calls);
        }
    }

    private void ExtractMemberAccessEdge(
        SemanticModel model, MemberAccessExpressionSyntax memberAccess,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(memberAccess);
        var target = symbolInfo.Symbol;
        if (target?.ContainingType == null) return;
        if (!IsTracked(target.ContainingType)) return;

        var containingType = FindContainingType(model, memberAccess);
        if (containingType == null) return;

        // Type-level References edge (existing behavior — valuable for module coupling)
        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);

        // Symbol-level References edge so properties/fields show incoming references
        EmitSymbolLevelEdge(model, memberAccess, target, edges, seen);
    }

    /// <summary>
    /// Handles null-conditional member access: obj?.Property produces MemberBindingExpressionSyntax
    /// (not MemberAccessExpressionSyntax). Method calls via ?. are already captured by the
    /// InvocationExpressionSyntax case; this handles property, field, and event access.
    /// </summary>
    private void ExtractMemberBindingEdge(
        SemanticModel model, MemberBindingExpressionSyntax memberBinding,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(memberBinding);
        var target = symbolInfo.Symbol;
        if (target?.ContainingType == null) return;
        if (!IsTracked(target.ContainingType)) return;

        // Type-level References edge
        var containingType = FindContainingType(model, memberBinding);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetTypeId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(target.ContainingType));

        if (sourceId != targetTypeId)
            AddEdge(edges, seen, sourceId, targetTypeId, EdgeKind.References);

        // Symbol-level edge (shared helper)
        EmitSymbolLevelEdge(model, memberBinding, target, edges, seen);
    }

    /// <summary>
    /// Shared helper: emit a symbol-level References edge from the containing method
    /// to a specific property, field, or event. Used by both MemberAccessExpressionSyntax
    /// and MemberBindingExpressionSyntax (null-conditional) handlers.
    /// </summary>
    private void EmitSymbolLevelEdge(
        SemanticModel model, SyntaxNode node, ISymbol target,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var caller = FindContainingMethodOrLocal(model, node);
        if (caller == null) return;

        var callerMethodId = GetMethodId(caller);
        switch (target)
        {
            case IPropertySymbol prop:
            {
                var fqn = RoslynSymbolExtractor.GetFullName(prop.ContainingType);
                var propId = prop.IsIndexer
                    ? SymbolIds.Property(fqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(prop)}]")
                    : SymbolIds.Property(fqn, prop.Name);
                AddEdge(edges, seen, callerMethodId, propId, EdgeKind.References);
                break;
            }
            case IFieldSymbol field:
            {
                var fqn = RoslynSymbolExtractor.GetFullName(field.ContainingType);
                AddEdge(edges, seen, callerMethodId, SymbolIds.Field(fqn, field.Name), EdgeKind.References);
                break;
            }
            case IEventSymbol evt:
            {
                var fqn = RoslynSymbolExtractor.GetFullName(evt.ContainingType);
                AddEdge(edges, seen, callerMethodId, SymbolIds.Property(fqn, evt.Name), EdgeKind.References);
                break;
            }
        }
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
            if (!IsTracked(argType)) continue;

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
        if (!IsTracked(referencedType)) return;

        var containingType = FindContainingType(model, typeofExpr);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(referencedType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Extract References edges for attribute types.
    /// [Obsolete] references System.ObsoleteAttribute (filtered by IsTracked).
    /// [MyCustomAttribute] references the source-defined attribute class.
    /// </summary>
    private void ExtractAttributeEdge(
        SemanticModel model, AttributeSyntax attribute,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var attrInfo = model.GetSymbolInfo(attribute);
        var attrCtor = attrInfo.Symbol as IMethodSymbol;
        if (attrCtor?.ContainingType == null) return;
        if (!IsTracked(attrCtor.ContainingType)) return;

        var containingType = FindContainingType(model, attribute);
        if (containingType == null) return;

        var sourceId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(containingType));
        var targetId = SymbolIds.Type(RoslynSymbolExtractor.GetFullName(attrCtor.ContainingType));

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Find the containing method, constructor, accessor, or synthesized ctor for a
    /// syntax node. Local functions, lambdas, and delegate expressions are skipped —
    /// they are not graph symbols, so their calls are attributed to the enclosing named
    /// scope instead. Returned IMethodSymbol may be a property/indexer/event accessor
    /// (MethodKind.PropertyGet etc.); GetMethodId routes those through AssociatedSymbol
    /// so the emitted edge source matches the extracted property/event graph node.
    /// For field and property initializers (`static Foo _x = Bar()` or
    /// `public int X { get; } = Compute()`), the containing "method" is the synthesized
    /// static (`.cctor`) or instance (`.ctor`) constructor that runs the initializer.
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
                case ParenthesizedLambdaExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    // Lambdas and delegates aren't graph symbols. Calls inside
                    // .Select(x => Foo(x)) or .Select(Foo) are attributed to the
                    // enclosing named method, same as local functions.
                    continue;
                case AccessorDeclarationSyntax accessor:
                    // Property/indexer/event accessor body (get/set/add/remove). Return the
                    // accessor IMethodSymbol; GetMethodId maps AssociatedSymbol → property or
                    // event ID so the edge source points at the extracted graph node.
                    return model.GetDeclaredSymbol(accessor) as IMethodSymbol;
                case MethodDeclarationSyntax method:
                    return model.GetDeclaredSymbol(method);
                case ConstructorDeclarationSyntax ctor:
                    return model.GetDeclaredSymbol(ctor);
                case VariableDeclaratorSyntax varDecl
                    when varDecl.Parent is VariableDeclarationSyntax varList
                      && varList.Parent is FieldDeclarationSyntax:
                {
                    // Field initializer expression: `static Lazy<T> _x = new(Load)` runs inside
                    // the synthesized .cctor; an instance field initializer runs inside every
                    // instance .ctor. Attribute the reference to the synthesized constructor
                    // so the target (Load) gets an incoming edge and the dead-code analyzer
                    // no longer flags it. For instance fields, the first InstanceConstructor
                    // is sufficient: the target's incoming-edge count is what matters, not
                    // per-ctor granularity.
                    var fieldSym = model.GetDeclaredSymbol(varDecl) as IFieldSymbol;
                    if (fieldSym?.ContainingType == null) return null;
                    return fieldSym.IsStatic
                        ? fieldSym.ContainingType.StaticConstructors.FirstOrDefault()
                        : fieldSym.ContainingType.InstanceConstructors.FirstOrDefault();
                }
                case PropertyDeclarationSyntax propDeclAncestor:
                {
                    var propSym = model.GetDeclaredSymbol(propDeclAncestor) as IPropertySymbol;
                    if (propSym?.ContainingType == null) return null;
                    if (propDeclAncestor.ExpressionBody != null)
                    {
                        // Expression-bodied property: `public int X => Compute();`. No
                        // AccessorDeclarationSyntax intermediate — parent chain lands directly
                        // on PropertyDeclarationSyntax. Treat as getter body; route via
                        // GetMethod so GetMethodId emits the property id.
                        return propSym.GetMethod;
                    }
                    if (propDeclAncestor.Initializer != null)
                    {
                        // Auto-property with initializer: `public int X { get; } = Compute()`.
                        // Attribute to the synthesized .cctor/.ctor (same as field init).
                        return propSym.IsStatic
                            ? propSym.ContainingType.StaticConstructors.FirstOrDefault()
                            : propSym.ContainingType.InstanceConstructors.FirstOrDefault();
                    }
                    // Regular `{ get; set; }` property — any reference inside an accessor
                    // body was already resolved by the AccessorDeclarationSyntax case above.
                    return null;
                }
                case IndexerDeclarationSyntax idxDeclAncestor when idxDeclAncestor.ExpressionBody != null:
                {
                    // Expression-bodied indexer: `public T this[int i] => _arr[i];`.
                    var idxSym = model.GetDeclaredSymbol(idxDeclAncestor) as IPropertySymbol;
                    return idxSym?.GetMethod;
                }
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
        // Property/indexer accessors (MethodKind.PropertyGet/Set) and event accessors
        // (MethodKind.EventAdd/Remove) are not extracted as independent graph symbols —
        // only the associated property/event is. Route the id through AssociatedSymbol
        // so edges sourced from or targeted at accessor bodies hit the property/event
        // graph node instead of producing dangling sources.
        if (method.AssociatedSymbol is IPropertySymbol prop)
        {
            var propFqn = RoslynSymbolExtractor.GetFullName(prop.ContainingType);
            return prop.IsIndexer
                ? SymbolIds.Property(propFqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(prop)}]")
                : SymbolIds.Property(propFqn, prop.Name);
        }
        if (method.AssociatedSymbol is IEventSymbol evt)
        {
            var evtFqn = RoslynSymbolExtractor.GetFullName(evt.ContainingType);
            return SymbolIds.Property(evtFqn, evt.Name);
        }

        var paramSig = CanonicalSymbolFormat.BuildParamSignature(method);
        return SymbolIds.Method(
            RoslynSymbolExtractor.GetFullName(method.ContainingType),
            method.Name, paramSig);
    }

    /// <summary>
    /// Returns true if the symbol is tracked in the workspace graph.
    /// A symbol is tracked if it's defined in source (same module) OR if it's a metadata
    /// symbol from another analyzed module (cross-assembly reference).
    /// Filters out BCL/framework types (System.*, Microsoft.*) regardless of origin.
    /// </summary>
    private bool IsTracked(ISymbol? symbol)
    {
        if (symbol == null) return false;

        // Filter BCL/framework types — check for "System" as a complete namespace segment.
        // Must not filter user namespaces like "SystemManager" or "SystemConfig".
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("Microsoft.", StringComparison.Ordinal)) return false;

        // Source symbol in the current compilation — always tracked
        if (symbol.DeclaringSyntaxReferences.Length > 0) return true;

        // Cross-module: metadata symbol from another analyzed module.
        // When Module A is compiled first and downgraded to a PE reference,
        // symbols from A appear as metadata symbols in Module B's compilation.
        // We track these if the containing assembly is a known workspace module.
        if (KnownModuleAssemblies != null
            && symbol.ContainingAssembly != null
            && KnownModuleAssemblies.Contains(symbol.ContainingAssembly.Name))
            return true;

        return false;
    }

    private static void AddEdge(
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen,
        string sourceId, string targetId, EdgeKind kind)
    {
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) return;
        // Guard against prefix-only IDs (e.g., "type:" with no name)
        if (sourceId.EndsWith(':') || targetId.EndsWith(':')) return;
        // Self-referencing edges carry no dependency information (recursion is valid C#
        // but a symbol always depends on itself — no analytical value).
        if (sourceId == targetId) return;
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
