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

    /// <summary>
    /// Repo-relative file path for the syntax tree currently being walked.
    /// Set by <see cref="Extract"/> on entry, threaded into <see cref="AddEdge"/>
    /// via the shared instance state so every source-derived edge can attach a
    /// <see cref="CallSite"/> without every helper signature growing a relPath
    /// parameter. Single-threaded by contract (the workspace analyzer walks
    /// one tree at a time per extractor instance — same contract as
    /// <see cref="KnownModuleAssemblies"/>).
    /// </summary>
    private string _currentRelPath = "";

    public List<Edge> Extract(SemanticModel model, SyntaxNode root)
        => Extract(model, root, relPath: "");

    public List<Edge> Extract(SemanticModel model, SyntaxNode root, string relPath)
    {
        _currentRelPath = relPath ?? "";
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
        // INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001: route through
        // GetMethodId (which canonicalizes to OriginalDefinition) so an
        // instantiated generic call-site lands on the source-declared
        // open-generic id, not the per-call-site constructed form.
        var targetId = GetMethodId(target);

        AddEdge(edges, seen, sourceId, targetId, EdgeKind.Calls,
            originatingNode: invocation, containingSymbolId: sourceId);
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
        AddEdge(edges, seen, sourceId, typeTargetId, EdgeKind.References,
            originatingNode: creation, containingSymbolId: sourceId);

        // Method-level edge: caller → .ctor. Only emit when the ctor itself is tracked
        // (source-declared or cross-module-known). Implicit default ctors produce metadata
        // symbols without DeclaringSyntaxReferences in the current compilation; IsTracked
        // accepts them when ContainingAssembly is a known workspace module.
        if (IsTracked(target))
        {
            var ctorTargetId = GetMethodId(target);
            AddEdge(edges, seen, sourceId, ctorTargetId, EdgeKind.Calls,
                originatingNode: creation, containingSymbolId: sourceId);
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
        var referencedSymbol = symbolInfo.Symbol
            ?? ResolveCandidateMethodGroup(symbolInfo);
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
                AddEdge(edges, seen, sourceId, targetId, EdgeKind.References,
                    originatingNode: identifier, containingSymbolId: sourceId);
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
            var fieldId = SymbolIds.Field(fqn, fieldSymbol.Name);
            AddEdge(edges, seen, callerMethodId, fieldId, EdgeKind.References,
                originatingNode: identifier, containingSymbolId: callerMethodId);
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
            var callerMethodId = GetMethodId(caller);
            var targetMethodId = GetMethodId(methodSymbol);
            AddEdge(edges, seen, callerMethodId, targetMethodId, EdgeKind.Calls,
                originatingNode: identifier, containingSymbolId: callerMethodId);
            AddInitializerOwnerReferenceEdge(model, identifier, targetMethodId, edges, seen);
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
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References,
                originatingNode: memberAccess, containingSymbolId: sourceId);

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
            AddEdge(edges, seen, sourceId, targetTypeId, EdgeKind.References,
                originatingNode: memberBinding, containingSymbolId: sourceId);

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
                AddEdge(edges, seen, callerMethodId, propId, EdgeKind.References,
                    originatingNode: node, containingSymbolId: callerMethodId);
                break;
            }
            case IFieldSymbol field:
            {
                var fqn = RoslynSymbolExtractor.GetFullName(field.ContainingType);
                var fieldId = SymbolIds.Field(fqn, field.Name);
                AddEdge(edges, seen, callerMethodId, fieldId, EdgeKind.References,
                    originatingNode: node, containingSymbolId: callerMethodId);
                break;
            }
            case IEventSymbol evt:
            {
                var fqn = RoslynSymbolExtractor.GetFullName(evt.ContainingType);
                var eventId = SymbolIds.Property(fqn, evt.Name);
                AddEdge(edges, seen, callerMethodId, eventId, EdgeKind.References,
                    originatingNode: node, containingSymbolId: callerMethodId);
                break;
            }
        }
    }

    private void AddInitializerOwnerReferenceEdge(
        SemanticModel model,
        SyntaxNode node,
        string targetId,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind)> seen)
    {
        var ownerId = FindInitializerOwnerId(model, node);
        if (ownerId == null) return;

        AddEdge(edges, seen, ownerId, targetId, EdgeKind.References,
            originatingNode: node,
            containingSymbolId: ownerId,
            properties: new Dictionary<string, string>
            {
                [EdgePropertyKeys.InitializerOwner] = EdgePropertyKeys.InitializerOwnerMethodGroup,
            });
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

    private static string? FindInitializerOwnerId(SemanticModel model, SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case VariableDeclaratorSyntax varDecl
                    when varDecl.Initializer != null
                      && varDecl.Initializer.Span.Contains(node.SpanStart)
                      && varDecl.Parent is VariableDeclarationSyntax varList
                      && varList.Parent is FieldDeclarationSyntax:
                {
                    var fieldSym = model.GetDeclaredSymbol(varDecl) as IFieldSymbol;
                    if (fieldSym?.ContainingType == null) return null;
                    var fqn = RoslynSymbolExtractor.GetFullName(fieldSym.ContainingType);
                    return SymbolIds.Field(fqn, fieldSym.Name);
                }
                case PropertyDeclarationSyntax propDecl
                    when propDecl.Initializer != null
                      && propDecl.Initializer.Span.Contains(node.SpanStart):
                {
                    var propSym = model.GetDeclaredSymbol(propDecl) as IPropertySymbol;
                    if (propSym?.ContainingType == null) return null;
                    var fqn = RoslynSymbolExtractor.GetFullName(propSym.ContainingType);
                    return propSym.IsIndexer
                        ? SymbolIds.Property(fqn, $"this[{CanonicalSymbolFormat.BuildIndexerParamSignature(propSym)}]")
                        : SymbolIds.Property(fqn, propSym.Name);
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

    /// <summary>
    /// Method-group references in target-typed contexts
    /// (<c>new Lazy&lt;T&gt;(Load)</c> via target-typed <c>new(Load)</c>;
    /// delegate ctor arguments; <c>Task.Run(MyMethod)</c>; etc.) bind
    /// through <see cref="SymbolInfo.CandidateSymbols"/> when the outer
    /// type-inference context has not yet narrowed the candidate set.
    /// <see cref="SymbolInfo.Symbol"/> stays <c>null</c> in that state
    /// even though Roslyn knows the candidate set — and the existing
    /// extractor early-returned, silently dropping the method-group
    /// edge. The empirical class: <c>BclReferenceLoader.References =
    /// new(Load)</c> and <c>RoslynCodeExecutor._cache = new(LoadHostBclReferences)</c>
    /// in Lifeblood self showed <c>find_references</c> hits but
    /// <c>dependants=0</c> on the target method (LB-INBOX-010).
    ///
    /// Roslyn primitive: <see cref="CandidateReason"/>. We accept the
    /// shapes where every candidate names the same method group
    /// (<see cref="CandidateReason.MemberGroup"/>) or where target-type
    /// resolution is the only missing piece
    /// (<see cref="CandidateReason.OverloadResolutionFailure"/>) and
    /// emit the edge to the first candidate's
    /// <see cref="ISymbol.OriginalDefinition"/>. For an overloaded
    /// method-group with a true ambiguity that nothing has resolved
    /// yet, this attributes the edge approximately — the only honest
    /// alternative is dropping the edge entirely, which is the
    /// pre-fix behavior the empirical false-positive class proves is
    /// worse for downstream tooling. INV-EXTRACT-METHOD-GROUP-CANDIDATE-001.
    /// </summary>
    private static ISymbol? ResolveCandidateMethodGroup(SymbolInfo symbolInfo)
    {
        if (symbolInfo.CandidateSymbols.Length == 0) return null;
        return symbolInfo.CandidateReason switch
        {
            CandidateReason.MemberGroup => symbolInfo.CandidateSymbols[0],
            CandidateReason.OverloadResolutionFailure => symbolInfo.CandidateSymbols[0],
            _ => null,
        };
    }

    private static string GetMethodId(IMethodSymbol method)
    {
        // Extension-method call-sites in instance form (`x.Foo()`) resolve to a
        // reduced IMethodSymbol whose parameter list drops the explicit `this`
        // receiver. The declaration path emits the unreduced symbol (`Foo(this T x)`).
        // Without ReducedFrom normalization here, every reduced-form call-site
        // lands on a non-matching symbol id, so the declared method shows
        // directDependants:0 and dead_code may flag it. Normalize to the unreduced
        // form before OriginalDefinition canonicalization so the consumer-side id
        // matches the producer-side declaration id byte-for-byte.
        // INV-EXTRACT-EXTENSION-REDUCED-001 / LB-TRACK-20260519-021.
        if (method.ReducedFrom != null) method = method.ReducedFrom;

        // Constructed-generic call-sites resolve to an IMethodSymbol whose
        // ContainingType + parameter types have been substituted with the
        // call's type arguments (e.g. `ApplyCap(stringArr, n)` binds to
        // `ApplyCap<string>(string[], int)`). Build the canonical id off
        // the source-declared open-generic form so dependants /
        // dead_code / blast_radius queries against the declared
        // `method:NS.T.ApplyCap(T[],int)` symbol find every instantiated
        // call-site. Mirrors INV-CANONICAL-001 / the same OriginalDefinition
        // discipline RoslynCompilationHost.FindReferences applies. The
        // empirical class (LB-INBOX-010 part 2): ToolHandler.ApplyCap
        // was called 5× yet showed `directDependants=0` because every
        // edge landed on a per-call-site instantiated symbol id that did
        // not match the source-declared generic id. INV-EXTRACT-METHOD-
        // ORIGINAL-DEFINITION-001.
        method = (IMethodSymbol)method.OriginalDefinition;

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

    private void AddEdge(
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen,
        string sourceId, string targetId, EdgeKind kind,
        SyntaxNode? originatingNode = null,
        string? containingSymbolId = null,
        IReadOnlyDictionary<string, string>? properties = null)
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
            Properties = properties ?? new Dictionary<string, string>(),
            CallSite = BuildCallSite(originatingNode, containingSymbolId ?? sourceId),
        });
    }

    /// <summary>
    /// Build a <see cref="CallSite"/> from a Roslyn syntax node and the
    /// canonical id of the enclosing declaration. Returns null when the node
    /// is null or has no source location (synthetic / compiler-generated).
    /// The file path is taken from the instance state set by
    /// <see cref="Extract(SemanticModel, SyntaxNode, string)"/> — callers do
    /// not pass it per-edge. Reads are zero-allocation past the
    /// <see cref="CallSite"/> instantiation itself.
    /// </summary>
    private CallSite? BuildCallSite(SyntaxNode? node, string containingSymbolId)
    {
        if (node == null) return null;
        var location = node.GetLocation();
        if (!location.IsInSource) return null;
        var span = location.GetLineSpan();
        return new CallSite
        {
            FilePath = !string.IsNullOrEmpty(_currentRelPath) ? _currentRelPath : span.Path,
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            EndLine = span.EndLinePosition.Line + 1,
            EndColumn = span.EndLinePosition.Character + 1,
            ContainingSymbolId = containingSymbolId ?? "",
        };
    }
}
