using Lifeblood.Application.Ports.Right;
using Lifeblood.Adapters.CSharp.Internal;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// C# adapter implementation of <see cref="IUserInputCanonicalizer"/>. Wraps
/// <see cref="PrimitiveAliasTable.Rewrite"/> so the Application-layer
/// resolver never takes a direct dependency on the C# adapter's alias table.
///
/// This class exists so that the composition root has a concrete type to
/// hand to the resolver: the table itself is <c>internal</c> to the adapter
/// assembly and cannot leak into Application, per INV-ADAPT-004. Future
/// language adapters publish their own canonicalizers alongside their own
/// alias tables.
/// </summary>
public sealed class CSharpUserInputCanonicalizer : IUserInputCanonicalizer
{
    public string Canonicalize(string userInput)
    {
        if (string.IsNullOrEmpty(userInput)) return userInput;
        return PrimitiveAliasTable.Rewrite(userInput);
    }
}
