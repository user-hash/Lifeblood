using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Extracts dependency edges from Roslyn's semantic model.
/// Inherits, Implements, Calls, References — all with semantic Evidence.
/// Handles: inheritance, interfaces, method/operator/conversion calls, constructor calls, member access,
/// type references, generic type arguments, typeof() expressions, attribute types.
/// </summary>
public sealed class RoslynEdgeExtractor
{
    private readonly CanonicalSymbolIdVersion _idVersion;
    private readonly CompilationSymbolIds? _compilationIds;

    public RoslynEdgeExtractor(CanonicalSymbolIdVersion idVersion = CanonicalSymbolIdVersion.LegacyV1)
    {
        _idVersion = idVersion;
    }

    internal RoslynEdgeExtractor(CompilationSymbolIds symbolIds) : this(symbolIds.Version)
    {
        _compilationIds = symbolIds;
    }

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
    private readonly Dictionary<SyntaxNode, IMethodSymbol?> _containingMethodCache = new();
    private readonly Dictionary<SyntaxNode, INamedTypeSymbol?> _containingTypeCache = new();
    private readonly Dictionary<ISymbol, string> _symbolIds = new(SymbolEqualityComparer.Default);

    public List<Edge> Extract(SemanticModel model, SyntaxNode root)
        => Extract(model, root, relPath: "");

    public List<Edge> Extract(SemanticModel model, SyntaxNode root, string relPath)
    {
        _currentRelPath = relPath ?? "";
        _containingMethodCache.Clear();
        _containingTypeCache.Clear();
        _symbolIds.Clear();
        var edges = new List<Edge>();
        var seen = new HashSet<(string, string, EdgeKind)>();
        var handledSemanticNodes = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);

        try
        {
            foreach (var node in root.DescendantNodes())
            {
                if (handledSemanticNodes.Contains(node))
                    continue;

                switch (node)
                {
                    case TypeDeclarationSyntax typeDecl:
                        ExtractInheritanceEdges(model, typeDecl, edges, seen);
                        break;

                    case InvocationExpressionSyntax invocation:
                        if (ExtractCallEdge(model, invocation, edges, seen))
                            MarkInvocationExpressionHandled(invocation, handledSemanticNodes);
                        break;

                    case BaseObjectCreationExpressionSyntax creation:
                        ExtractConstructorCallEdge(model, creation, edges, seen);
                        break;

                    case IdentifierNameSyntax identifier:
                        ExtractReferenceEdge(model, identifier, edges, seen);
                        break;

                    case MemberAccessExpressionSyntax memberAccess:
                        if (ExtractMemberAccessEdge(model, memberAccess, edges, seen)
                            && memberAccess.Name is IdentifierNameSyntax memberName)
                        {
                            handledSemanticNodes.Add(memberName);
                        }
                        break;

                    case MemberBindingExpressionSyntax memberBinding:
                        if (ExtractMemberBindingEdge(model, memberBinding, edges, seen)
                            && memberBinding.Name is IdentifierNameSyntax bindingName)
                        {
                            handledSemanticNodes.Add(bindingName);
                        }
                        break;

                    case ElementAccessExpressionSyntax or ElementBindingExpressionSyntax:
                        if (model.GetSymbolInfo(node).Symbol is IPropertySymbol indexer
                            && IsTracked(indexer.ContainingType))
                        {
                            EmitSymbolLevelEdge(model, node, indexer, edges, seen);
                        }
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

            foreach (var operationRoot in RoslynOperationFacts.FindOperationRoots(model, root))
            {
                foreach (var operation in operationRoot.DescendantsAndSelf())
                {
                    switch (operation)
                    {
                        case IBinaryOperation binary:
                            AddOperatorCall(binary.OperatorMethod, operation);
                            break;
                        case IUnaryOperation unary:
                            AddOperatorCall(unary.OperatorMethod, operation);
                            break;
                        case IIncrementOrDecrementOperation increment:
                            AddOperatorCall(increment.OperatorMethod, operation);
                            break;
                        case ICompoundAssignmentOperation compound:
                            AddOperatorCall(compound.OperatorMethod, operation);
                            AddOperatorCall(compound.InConversion.MethodSymbol, operation);
                            AddOperatorCall(compound.OutConversion.MethodSymbol, operation);
                            break;
                        case IConversionOperation conversion:
                            AddOperatorCall(conversion.OperatorMethod, operation);
                            break;
                        case IArgumentOperation argument:
                            AddOperatorCall(argument.InConversion.MethodSymbol, operation);
                            AddOperatorCall(argument.OutConversion.MethodSymbol, operation);
                            break;
                        case IInvocationOperation invocation when invocation.TargetMethod.MethodKind
                            is MethodKind.UserDefinedOperator or MethodKind.Conversion:
                            AddOperatorCall(invocation.TargetMethod, operation);
                            break;
                    }
                }
            }

            return edges;

            void AddOperatorCall(IMethodSymbol? target, IOperation operation)
            {
                if (target == null || !IsTracked(target)) return;
                var caller = FindContainingMethodOrLocal(model, operation.Syntax);
                if (caller == null) return;
                var sourceId = GetSymbolId(caller);
                AddEdge(edges, seen, sourceId, GetSymbolId(target), EdgeKind.Calls,
                    originatingNode: operation.Syntax, containingSymbolId: sourceId);
            }
        }
        finally
        {
            // Release standalone tree-local symbol keys. A workspace pass owns
            // its shared IDs separately; the formatter remains the authority.
            _symbolIds.Clear();
        }
    }

    private static void MarkInvocationExpressionHandled(
        InvocationExpressionSyntax invocation,
        ISet<SyntaxNode> handledSemanticNodes)
    {
        switch (invocation.Expression)
        {
            case IdentifierNameSyntax identifier:
                handledSemanticNodes.Add(identifier);
                break;
            case MemberAccessExpressionSyntax memberAccess:
                handledSemanticNodes.Add(memberAccess);
                if (memberAccess.Name is IdentifierNameSyntax memberName)
                    handledSemanticNodes.Add(memberName);
                break;
            case MemberBindingExpressionSyntax memberBinding:
                handledSemanticNodes.Add(memberBinding);
                if (memberBinding.Name is IdentifierNameSyntax bindingName)
                    handledSemanticNodes.Add(bindingName);
                break;
        }
    }

    private void ExtractInheritanceEdges(
        SemanticModel model, TypeDeclarationSyntax typeDecl,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var typeSymbol = model.GetDeclaredSymbol(typeDecl);
        if (typeSymbol == null) return;

        var sourceId = GetSymbolId(typeSymbol);

        // Base type → Inherits (only source-defined bases)
        if (typeSymbol.BaseType != null
            && typeSymbol.BaseType.SpecialType != SpecialType.System_Object
            && typeSymbol.BaseType.SpecialType != SpecialType.System_ValueType
            && typeSymbol.BaseType.SpecialType != SpecialType.System_Enum
            && IsTracked(typeSymbol.BaseType))
        {
            var targetId = GetSymbolId(typeSymbol.BaseType);
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.Inherits);
        }

        // Interfaces → Implements (class/struct) or Inherits (interface).
        // INV-EXTRACT-IFACE-INHERIT-001: an interface that extends another
        // interface is inheritance, not implementation. Pre-F3c the loop
        // emitted Implements unconditionally, which made composite-interface
        // traversal in port_health / authority_report invisible (they walk
        // Inherits semantics for the inheritance closure).
        var ifaceEdgeKind = typeSymbol.TypeKind == TypeKind.Interface
            ? EdgeKind.Inherits
            : EdgeKind.Implements;
        foreach (var iface in typeSymbol.Interfaces)
        {
            if (!IsTracked(iface)) continue;
            var targetId = GetSymbolId(iface);
            AddEdge(edges, seen, sourceId, targetId, ifaceEdgeKind);
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
                        GetSymbolId(method), GetSymbolId(method.OverriddenMethod),
                        EdgeKind.Overrides);
                    break;
                }
                case IPropertySymbol prop when prop.OverriddenProperty != null
                    && IsTracked(prop.OverriddenProperty):
                {
                    var propSourceId = GetSymbolId(prop);
                    var propTargetId = GetSymbolId(prop.OverriddenProperty);
                    AddEdge(edges, seen, propSourceId, propTargetId, EdgeKind.Overrides);
                    break;
                }
                case IEventSymbol evt when evt.OverriddenEvent != null
                    && IsTracked(evt.OverriddenEvent):
                {
                    AddEdge(edges, seen,
                        GetSymbolId(evt),
                        GetSymbolId(evt.OverriddenEvent),
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
                            GetSymbolId(implMethod), GetSymbolId(ifaceMethod),
                            EdgeKind.Implements);
                        break;
                    }
                    case IPropertySymbol ifaceProp when impl is IPropertySymbol implProp:
                    {
                        var implPropId = GetSymbolId(implProp);
                        var ifacePropId = GetSymbolId(ifaceProp);
                        AddEdge(edges, seen, implPropId, ifacePropId, EdgeKind.Implements);
                        break;
                    }
                    case IEventSymbol ifaceEvt when impl is IEventSymbol implEvt:
                    {
                        AddEdge(edges, seen,
                            GetSymbolId(implEvt),
                            GetSymbolId(ifaceEvt),
                            EdgeKind.Implements);
                        break;
                    }
                }
            }
        }
    }

    private bool ExtractCallEdge(
        SemanticModel model, InvocationExpressionSyntax invocation,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(invocation);
        var target = symbolInfo.Symbol as IMethodSymbol;
        if (target?.ContainingType == null) return false;
        // A delegate-valued field/property invocation binds the invocation node
        // to the compiler-synthesized DelegateInvoke method, not to the member
        // that supplied the delegate instance. DelegateInvoke has no extracted
        // graph symbol. Let the child identifier/member-access handler retain
        // ownership so `_callback()` and `feature.Probe()` produce the real
        // field/property References edge instead of a dangling Calls edge.
        if (target.MethodKind == MethodKind.DelegateInvoke) return false;
        if (!IsTracked(target)) return false;

        var caller = FindContainingMethodOrLocal(model, invocation);
        if (caller == null) return false;

        var sourceId = GetSymbolId(caller);
        // INV-EXTRACT-METHOD-ORIGINAL-DEFINITION-001: route through
        // GetMethodId (which canonicalizes to OriginalDefinition) so an
        // instantiated generic call-site lands on the source-declared
        // open-generic id, not the per-call-site constructed form.
        var targetId = GetSymbolId(target);

        AddEdge(edges, seen, sourceId, targetId, EdgeKind.Calls,
            originatingNode: invocation, containingSymbolId: sourceId);

        // The legacy identifier pass also records initializer ownership for
        // direct and qualified method invocations in field/property initializers.
        // Once InvocationExpression owns the semantic bind, preserve that
        // distinct References edge here before suppressing the child identifier.
        var invocationIdentifier = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } => name,
            MemberBindingExpressionSyntax { Name: IdentifierNameSyntax name } => name,
            _ => null,
        };
        if (invocationIdentifier != null)
        {
            AddInitializerOwnerReferenceEdge(
                model,
                invocationIdentifier,
                targetId,
                edges,
                seen);
        }

        // InvocationExpression owns the method binding. When its expression is
        // a member access, project the type-level coupling here as well so the
        // child MemberAccessExpression does not repeat the same Roslyn bind.
        // Delegate-valued fields/properties are intentionally excluded above:
        // their invocation target is System.Action.Invoke (untracked), so the
        // child identifier/member-access path still records the field/property
        // reference rather than being marked handled.
        if (invocation.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
        {
            var containingType = FindContainingType(model, invocation);
            if (containingType != null)
            {
                var containingTypeId = GetSymbolId(containingType);
                var targetTypeId = GetSymbolId(target.ContainingType);
                if (containingTypeId != targetTypeId)
                {
                    AddEdge(edges, seen, containingTypeId, targetTypeId, EdgeKind.References,
                        originatingNode: invocation.Expression,
                        containingSymbolId: containingTypeId);
                }
            }
        }

        return true;
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

        var sourceId = GetSymbolId(caller);

        // Type-level edge: caller → containing type (module-coupling signal).
        var typeTargetId = GetSymbolId(target.ContainingType);
        AddEdge(edges, seen, sourceId, typeTargetId, EdgeKind.References,
            originatingNode: creation, containingSymbolId: sourceId);

        // Method-level edge: caller → .ctor. Only emit when the ctor itself is tracked
        // (source-declared or cross-module-known). Implicit default ctors produce metadata
        // symbols without DeclaringSyntaxReferences in the current compilation; IsTracked
        // accepts them when ContainingAssembly is a known workspace module.
        if (IsTracked(target))
        {
            var ctorTargetId = GetSymbolId(target);
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
            var sourceId = GetSymbolId(containingType);
            var targetId = GetSymbolId(referencedType);
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
            var callerMethodId = GetSymbolId(caller);
            var fieldId = GetSymbolId(fieldSymbol);
            AddEdge(edges, seen, callerMethodId, fieldId, EdgeKind.References,
                originatingNode: identifier, containingSymbolId: callerMethodId);
            return;
        }

        // INV-EXTRACT-PROPERTY-READ-001: bare-identifier property/event reads from sibling
        // members (read OR write — LHS of an assignment is also IdentifierNameSyntax) emit
        // a symbol-level References edge. Without this arm, C#'s common style convention of
        // dropping the `this.` prefix silently dropped ~89% of private-property incoming edges
        // workspace-wide; `find_references` resolved them via Roslyn's semantic walk, but
        // `dependants` / `dead_code` / `blast_radius` walk the edge graph and missed them.
        // Member-access form (`this.X`, `obj.X`) is handled by ExtractMemberAccessEdge.
        if (referencedSymbol is IPropertySymbol or IEventSymbol)
        {
            if (referencedSymbol.ContainingType != null
                && IsTracked(referencedSymbol.ContainingType))
            {
                EmitSymbolLevelEdge(model, identifier, referencedSymbol, edges, seen);
            }
            return;
        }

        // Method-group references (new Lazy<T>(Load), event += Handler, Where(predicate))
        if (referencedSymbol is IMethodSymbol methodSymbol
            && methodSymbol.MethodKind != MethodKind.Constructor
            && methodSymbol.ContainingType != null
            && IsTracked(methodSymbol))
        {
            EmitMethodGroupEdge(model, identifier, methodSymbol, edges, seen);
        }
    }

    private bool ExtractMemberAccessEdge(
        SemanticModel model, MemberAccessExpressionSyntax memberAccess,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(memberAccess);
        var target = symbolInfo.Symbol ?? ResolveCandidateMethodGroup(symbolInfo);
        if (target?.ContainingType == null) return false;
        if (!IsTracked(target.ContainingType)) return false;

        var containingType = FindContainingType(model, memberAccess);
        if (containingType == null) return false;

        // Type-level References edge (existing behavior — valuable for module coupling)
        var sourceId = GetSymbolId(containingType);
        var targetId = GetSymbolId(target.ContainingType);

        if (sourceId != targetId)
        {
            // An unresolved overloaded method group (notably inside nameof)
            // did not previously own this type reference; its left-hand type
            // identifier did. Preserve that precise provenance span while the
            // parent now owns the single semantic bind.
            var typeReferenceNode = symbolInfo.Symbol == null
                ? memberAccess.Expression
                : memberAccess;
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References,
                originatingNode: typeReferenceNode, containingSymbolId: sourceId);
        }

        // Symbol-level References edge so properties/fields show incoming references
        EmitSymbolLevelEdge(model, memberAccess, target, edges, seen);

        if (target is IMethodSymbol methodSymbol
            && methodSymbol.MethodKind != MethodKind.Constructor)
        {
            // Preserve the identifier-level source span emitted by the legacy
            // identifier pass while moving semantic ownership to this parent.
            EmitMethodGroupEdge(model, memberAccess.Name, methodSymbol, edges, seen);
        }

        return target is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol;
    }

    /// <summary>
    /// Handles null-conditional member access: obj?.Property produces MemberBindingExpressionSyntax
    /// (not MemberAccessExpressionSyntax). Method calls via ?. are already captured by the
    /// InvocationExpressionSyntax case; this handles property, field, and event access.
    /// </summary>
    private bool ExtractMemberBindingEdge(
        SemanticModel model, MemberBindingExpressionSyntax memberBinding,
        List<Edge> edges, HashSet<(string, string, EdgeKind)> seen)
    {
        var symbolInfo = model.GetSymbolInfo(memberBinding);
        var target = symbolInfo.Symbol ?? ResolveCandidateMethodGroup(symbolInfo);
        if (target?.ContainingType == null) return false;
        if (!IsTracked(target.ContainingType)) return false;

        // Type-level References edge
        var containingType = FindContainingType(model, memberBinding);
        if (containingType == null) return false;

        var sourceId = GetSymbolId(containingType);
        var targetTypeId = GetSymbolId(target.ContainingType);

        if (sourceId != targetTypeId)
            AddEdge(edges, seen, sourceId, targetTypeId, EdgeKind.References,
                originatingNode: memberBinding, containingSymbolId: sourceId);

        // Symbol-level edge (shared helper)
        EmitSymbolLevelEdge(model, memberBinding, target, edges, seen);

        if (target is IMethodSymbol methodSymbol
            && methodSymbol.MethodKind != MethodKind.Constructor)
        {
            EmitMethodGroupEdge(model, memberBinding.Name, methodSymbol, edges, seen);
        }

        return target is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol;
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

        var callerMethodId = GetSymbolId(caller);
        switch (target)
        {
            case IPropertySymbol prop:
            {
                var propId = GetSymbolId(prop);
                AddEdge(edges, seen, callerMethodId, propId, EdgeKind.References,
                    originatingNode: node, containingSymbolId: callerMethodId);
                break;
            }
            case IFieldSymbol field:
            {
                var fieldId = GetSymbolId(field);
                AddEdge(edges, seen, callerMethodId, fieldId, EdgeKind.References,
                    originatingNode: node, containingSymbolId: callerMethodId);
                break;
            }
            case IEventSymbol evt:
            {
                var eventId = GetSymbolId(evt);
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

    private void EmitMethodGroupEdge(
        SemanticModel model,
        SyntaxNode node,
        IMethodSymbol methodSymbol,
        List<Edge> edges,
        HashSet<(string, string, EdgeKind)> seen)
    {
        if (!IsTracked(methodSymbol)) return;

        var caller = FindContainingMethodOrLocal(model, node);
        if (caller == null) return;

        var callerMethodId = GetSymbolId(caller);
        var targetMethodId = GetSymbolId(methodSymbol);
        AddEdge(edges, seen, callerMethodId, targetMethodId, EdgeKind.Calls,
            originatingNode: node, containingSymbolId: callerMethodId);
        AddInitializerOwnerReferenceEdge(model, node, targetMethodId, edges, seen);
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

        var sourceId = GetSymbolId(containingType);

        foreach (var typeArg in genericName.TypeArgumentList.Arguments)
        {
            var typeInfo = model.GetTypeInfo(typeArg);
            var argType = typeInfo.Type as INamedTypeSymbol;
            if (argType == null) continue;
            if (!IsTracked(argType)) continue;

            var targetId = GetSymbolId(argType);
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

        var sourceId = GetSymbolId(containingType);
        var targetId = GetSymbolId(referencedType);

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

        var sourceId = GetSymbolId(containingType);
        var targetId = GetSymbolId(attrCtor.ContainingType);

        if (sourceId != targetId)
            AddEdge(edges, seen, sourceId, targetId, EdgeKind.References);
    }

    /// <summary>
    /// Find the containing method, operator, conversion, destructor, accessor, or ctor for a
    /// syntax node. Local functions, lambdas, and delegate expressions are skipped —
    /// they are not graph symbols, so their calls are attributed to the enclosing named
    /// scope instead. Returned IMethodSymbol may be a property/indexer/event accessor
    /// (MethodKind.PropertyGet etc.); GetMethodId routes those through AssociatedSymbol
    /// so the emitted edge source matches the extracted property/event graph node.
    /// For field and property initializers (`static Foo _x = Bar()` or
    /// `public int X { get; } = Compute()`), the containing "method" is the synthesized
    /// static (`.cctor`) or instance (`.ctor`) constructor that runs the initializer.
    /// </summary>
    private IMethodSymbol? FindContainingMethodOrLocal(SemanticModel model, SyntaxNode node)
    {
        var owner = FindContainingMethodOwner(node);
        if (owner == null) return null;
        if (_containingMethodCache.TryGetValue(owner, out var cached))
            return cached;

        var resolved = ResolveContainingMethod(model, owner);
        _containingMethodCache[owner] = resolved;
        return resolved;
    }

    private static SyntaxNode? FindContainingMethodOwner(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    continue;
                case AccessorDeclarationSyntax:
                case BaseMethodDeclarationSyntax:
                    return ancestor;
                case VariableDeclaratorSyntax variable
                    when variable.Parent is VariableDeclarationSyntax variableList
                      && variableList.Parent is FieldDeclarationSyntax:
                    return variable;
                case PropertyDeclarationSyntax:
                    return ancestor;
                case IndexerDeclarationSyntax indexer when indexer.ExpressionBody != null:
                    return indexer;
            }
        }

        return null;
    }

    private static IMethodSymbol? ResolveContainingMethod(SemanticModel model, SyntaxNode owner)
    {
        switch (owner)
        {
            case AccessorDeclarationSyntax:
            case BaseMethodDeclarationSyntax:
                return model.GetDeclaredSymbol(owner) as IMethodSymbol;
            case VariableDeclaratorSyntax variable:
            {
                var field = model.GetDeclaredSymbol(variable) as IFieldSymbol;
                return field?.IsStatic == true
                    ? field.ContainingType.StaticConstructors.FirstOrDefault()
                    : field?.ContainingType.InstanceConstructors.FirstOrDefault();
            }
            case PropertyDeclarationSyntax property:
            {
                var symbol = model.GetDeclaredSymbol(property) as IPropertySymbol;
                if (property.ExpressionBody != null) return symbol?.GetMethod;
                if (property.Initializer == null) return null;
                return symbol?.IsStatic == true
                    ? symbol.ContainingType.StaticConstructors.FirstOrDefault()
                    : symbol?.ContainingType.InstanceConstructors.FirstOrDefault();
            }
            case IndexerDeclarationSyntax indexer:
                return (model.GetDeclaredSymbol(indexer) as IPropertySymbol)?.GetMethod;
            default:
                return null;
        }
    }

    private string? FindInitializerOwnerId(SemanticModel model, SyntaxNode node)
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
                    return GetSymbolId(fieldSym);
                }
                case PropertyDeclarationSyntax propDecl
                    when propDecl.Initializer != null
                      && propDecl.Initializer.Span.Contains(node.SpanStart):
                {
                    var propSym = model.GetDeclaredSymbol(propDecl) as IPropertySymbol;
                    if (propSym?.ContainingType == null) return null;
                    return GetSymbolId(propSym);
                }
            }
        }

        return null;
    }

    private INamedTypeSymbol? FindContainingType(SemanticModel model, SyntaxNode node)
    {
        SyntaxNode? owner = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        owner ??= node.Ancestors().OfType<EnumDeclarationSyntax>().FirstOrDefault();
        if (owner == null) return null;
        if (_containingTypeCache.TryGetValue(owner, out var cached))
            return cached;

        var resolved = FindContainingTypeUncached(model, node);
        _containingTypeCache[owner] = resolved;
        return resolved;
    }

    private static INamedTypeSymbol? FindContainingTypeUncached(SemanticModel model, SyntaxNode node)
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

    private string GetSymbolId(ISymbol symbol)
    {
        if (_compilationIds != null) return _compilationIds.Get(symbol);
        if (_symbolIds.TryGetValue(symbol, out var id)) return id;
        return _symbolIds[symbol] = CanonicalSymbolFormat.BuildSymbolId(symbol, _idVersion);
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
            Properties = properties ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
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
