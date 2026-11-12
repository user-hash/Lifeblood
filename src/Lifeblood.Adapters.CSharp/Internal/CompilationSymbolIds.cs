using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Shares canonical ID strings between tree workers in one compilation pass.
/// Only extracted strings escape the pass; compiler symbols stay in this cache.
/// </summary>
internal sealed class CompilationSymbolIds(CanonicalSymbolIdVersion version)
{
    private readonly ConcurrentDictionary<ISymbol, string> _ids = new(SymbolEqualityComparer.Default);

    internal CanonicalSymbolIdVersion Version { get; } = version;

    internal string Get(ISymbol symbol)
        => _ids.GetOrAdd(symbol, static (key, policy) => CanonicalSymbolFormat.BuildSymbolId(key, policy), Version);
}
