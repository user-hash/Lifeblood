using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Internal seam between <see cref="RoslynCompilationHost"/> and the
/// per-tool service classes extracted out of it (S8 adapter thinning
/// chain). The host implements this; extracted services depend on the
/// interface, never on the concrete host type. Eliminates the type
/// cycle that an extracted service holding a host reference would
/// create — INV-ADAPTER-THIN-001 acceptance requires "no new cycles".
///
/// Three members:
/// <list type="bullet">
/// <item><see cref="Compilations"/> — read-only access to the loaded
///   compilation set for full-tree walks.</item>
/// <item><see cref="ResolveFromSource"/> — source-resolved
///   <see cref="ISymbol"/> lookup with metadata fallback.</item>
/// <item><see cref="BuildSymbolId"/> — canonical Lifeblood id for any
///   resolved Roslyn symbol. Static-style behavior surfaced as an
///   instance member so the interface is mockable for tests.</item>
/// </list>
/// </summary>
internal interface IRoslynLookup
{
    IReadOnlyDictionary<string, CSharpCompilation> Compilations { get; }
    ISymbol? ResolveFromSource(string symbolId);
    string BuildSymbolId(ISymbol symbol);
}
