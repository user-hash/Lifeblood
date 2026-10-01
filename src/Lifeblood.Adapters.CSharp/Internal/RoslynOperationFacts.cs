using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Shared Roslyn operation-root discovery, read/write classification and source
/// type enumeration for graph extraction and occurrence-level analysis.
/// </summary>
internal static class RoslynOperationFacts
{
    /// <summary>
    /// Independent roots in syntax order, including attributes and default
    /// parameter values outside method-body operation trees.
    /// </summary>
    internal static IReadOnlyList<IOperation> FindOperationRoots(
        SemanticModel model,
        SyntaxNode syntaxRoot,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<IOperation>();
        var seen = new HashSet<IOperation>(ReferenceEqualityComparer.Instance);
        foreach (var node in syntaxRoot.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = model.GetOperation(node, cancellationToken);
            if (operation?.Parent != null || operation == null || !seen.Add(operation)) continue;
            roots.Add(operation);
        }
        return roots;
    }

    /// <summary>
    /// True if the reference operation sits in a write position: assignment
    /// target (simple / compound / coalesce), increment/decrement target, or a
    /// ref/out argument. Everything else is a read. Compound assignment counts
    /// as a write (the member IS assigned) even though it also reads.
    /// </summary>
    internal static bool IsWriteContext(IOperation reference)
    {
        switch (reference.Parent)
        {
            case IAssignmentOperation a:
                return ReferenceEquals(a.Target, reference);
            case IIncrementOrDecrementOperation inc:
                return ReferenceEquals(inc.Target, reference);
            case IArgumentOperation arg:
                return arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out;
            case IEventAssignmentOperation eventAssignment:
                return ReferenceEquals(eventAssignment.EventReference, reference);
            default:
                return false;
        }
    }

    /// <summary>All named types (including nested) declared anywhere under a namespace.</summary>
    internal static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in EnumerateTypes(child)) yield return t;
            else if (member is INamedTypeSymbol type)
                foreach (var t in EnumerateWithNested(type)) yield return t;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateWithNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var t in EnumerateWithNested(nested)) yield return t;
    }
}
