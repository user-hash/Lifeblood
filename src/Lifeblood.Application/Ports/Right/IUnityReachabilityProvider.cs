using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Right-side port that classifies a symbol as reachable through a
/// runtime-dispatch path that static analysis cannot see. Today the
/// reference adapter knows about Unity's framework-dispatch surface —
/// entrypoint attributes, MonoBehaviour magic methods, editor lifecycle
/// hooks. Future implementations can plug in other framework-dispatch
/// stories (ASP.NET attribute routing, MAUI handlers, MEF imports)
/// without touching the dead-code analyzer's signature.
///
/// Implementations are pure functions of (graph, symbol). They never
/// hold session state, never mutate the graph, and may consult the
/// symbol's <see cref="Symbol.Properties"/> dictionary for
/// extractor-recorded metadata (the C# adapter records
/// <c>Properties["attributes"]</c> and base-chain facts for this
/// purpose).
/// </summary>
public interface IUnityReachabilityProvider
{
    /// <summary>
    /// True when <paramref name="sym"/> is reachable through a runtime
    /// dispatch path the provider knows about. <paramref name="reason"/>
    /// is populated with a short human-readable explanation
    /// (e.g. "MonoBehaviour magic method 'Update'") when the result is
    /// true; empty otherwise. False / empty for symbols the provider
    /// has no opinion about — the caller is expected to combine
    /// multiple reachability providers if needed.
    /// </summary>
    bool IsRuntimeReachable(SemanticGraph graph, Symbol sym, out string reason);
}
