using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Read-only typed accessor for the C# adapter's loaded semantic state.
/// Constructed once per <see cref="RoslynWorkspaceAnalyzer.AnalyzeWorkspace"/>
/// call. Consumed by <see cref="RoslynCodeExecutor"/> and any other tool that
/// needs read access to the loaded compilations + graph.
///
/// This is the script-globals object passed to <c>lifeblood_execute</c> scripts.
/// Scripts access it via top-level identifiers <c>Graph</c>, <c>Compilations</c>,
/// <c>ModuleDependencies</c> — <c>CSharpScript.RunAsync&lt;TGlobals&gt;</c>
/// exposes instance members at script scope, so the script source reads them
/// as bare identifiers, no <c>this.</c> needed.
///
/// Lifecycle: passive POCO with three property assignments. Constructed in
/// <c>GraphSession.Load</c> after the analyzer reports its compilations and
/// graph; shared by reference across consumers (script host today; debuggers,
/// visualizers, custom linters tomorrow). The view never holds locks, never
/// caches anything beyond the references it was given, and never mutates
/// the underlying state — it is purely a typed handle.
///
/// See INV-VIEW-001..003 in the Lifeblood CLAUDE.md.
/// </summary>
public sealed class RoslynSemanticView
{
    /// <summary>
    /// All retained <see cref="CSharpCompilation"/> instances indexed by
    /// module name. Use this to reach Roslyn's full SemanticModel /
    /// GlobalNamespace / SyntaxTrees / References for any module in the
    /// loaded workspace. Read-only — script code can call <c>WithX()</c>
    /// methods on a compilation, but those return new instances and do
    /// not mutate Lifeblood's state.
    /// </summary>
    public IReadOnlyDictionary<string, CSharpCompilation> Compilations { get; }

    /// <summary>
    /// The loaded <see cref="SemanticGraph"/> — Lifeblood's universal
    /// symbol model. Use this for queries that span modules without
    /// dropping to Roslyn: <c>SymbolsOfKind</c>, <c>GetIncomingEdgeIndexes</c>,
    /// <c>GetOutgoingEdgeIndexes</c>, <c>GetSymbol</c>, <c>FindByShortName</c>.
    /// </summary>
    public SemanticGraph Graph { get; }

    /// <summary>
    /// Module dependency map (module name → array of upstream module names).
    /// Useful for scripts that walk the cross-assembly graph (architectural
    /// metrics, layering checks, dependency density).
    /// </summary>
    public IReadOnlyDictionary<string, string[]> ModuleDependencies { get; }

    public RoslynSemanticView(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        SemanticGraph graph,
        IReadOnlyDictionary<string, string[]> moduleDependencies)
    {
        Compilations = compilations;
        Graph = graph;
        ModuleDependencies = moduleDependencies;
    }
}
