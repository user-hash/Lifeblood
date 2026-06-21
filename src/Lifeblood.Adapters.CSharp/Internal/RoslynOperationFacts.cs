using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Operation-tree primitives shared by the dead-wire / feature-switch family of
/// extractors (<see cref="RoslynWireAuditExtractor"/>,
/// <see cref="RoslynFeatureSwitchExtractor"/>). Read/write classification of a
/// field / property reference and source-type enumeration are identical across
/// those tools, so there is exactly one implementation here. Operation-tree only;
/// never regex.
/// </summary>
internal static class RoslynOperationFacts
{
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
