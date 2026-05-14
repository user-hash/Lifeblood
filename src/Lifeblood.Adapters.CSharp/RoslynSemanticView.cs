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

    /// <summary>
    /// Sandbox-introspection cheat sheet. Scripts call <c>Help</c> from
    /// the top level and get back the available globals, common
    /// query shapes, and the EdgeKind / SymbolKind value names so you
    /// don't have to remember the enum members.
    /// </summary>
    public string Help => HelpText;

    private static readonly string HelpText =
        "lifeblood_execute sandbox cheat sheet\n" +
        "=====================================\n" +
        "Available globals:\n" +
        "  Graph                : Lifeblood.Domain.Graph.SemanticGraph\n" +
        "  Compilations         : IReadOnlyDictionary<string, CSharpCompilation>\n" +
        "  ModuleDependencies   : IReadOnlyDictionary<string, string[]>\n" +
        "  Help                 : this string\n" +
        "  EdgesOfKind(name)    : IEnumerable<Edge>   filtered by EdgeKind name\n" +
        "  SymbolsOfKind(name)  : IEnumerable<Symbol> filtered by SymbolKind name\n" +
        "\n" +
        "EdgeKind names: Calls, Contains, Inherits, Implements, References,\n" +
        "                DependsOn, Override.\n" +
        "SymbolKind names: Module, Namespace, Type, Method, Field, Property,\n" +
        "                  Event, File, Parameter, Variable, Local.\n" +
        "\n" +
        "Common queries:\n" +
        "  Graph.Symbols.Count                                       // total symbol count\n" +
        "  Graph.Edges.Count\n" +
        "  Graph.GetSymbol(\"type:My.Type\")\n" +
        "  Graph.FindByShortName(\"MyType\")\n" +
        "  Graph.SymbolsOfKind(SymbolKind.Type).Count()\n" +
        "  EdgesOfKind(\"Calls\").Count()\n" +
        "  Compilations[\"My.Module\"].SyntaxTrees.Count()\n" +
        "  ModuleDependencies[\"My.Module\"]                           // upstream module names\n" +
        "\n" +
        "Notes:\n" +
        "  - Graph is read-only. Mutating calls have no effect.\n" +
        "  - Console.WriteLine output is captured and returned.\n" +
        "  - Default timeout 5000ms; pass `timeoutMs` in the request to extend.\n" +
        "  - Network / process / reflection-emit / file-write are blocked.\n";

    /// <summary>
    /// String-accepting wrapper over
    /// <see cref="SemanticGraph.SymbolsOfKind(SymbolKind)"/>. Sandbox
    /// ergonomics — scripts can write
    /// <c>SymbolsOfKind("Method")</c> instead of having to import
    /// the enum. Unknown kind names return an empty sequence (no throw).
    /// </summary>
    public IEnumerable<Symbol> SymbolsOfKind(string kindName)
    {
        if (string.IsNullOrEmpty(kindName)) return System.Linq.Enumerable.Empty<Symbol>();
        if (!System.Enum.TryParse<SymbolKind>(kindName, ignoreCase: true, out var kind))
            return System.Linq.Enumerable.Empty<Symbol>();
        return Graph.SymbolsOfKind(kind);
    }

    /// <summary>
    /// String-accepting filter over <see cref="SemanticGraph.Edges"/>.
    /// Sandbox ergonomic — scripts use <c>EdgesOfKind("Calls")</c>
    /// instead of importing <c>EdgeKind</c>. Unknown kind names return
    /// an empty sequence.
    /// </summary>
    public IEnumerable<Edge> EdgesOfKind(string kindName)
    {
        if (string.IsNullOrEmpty(kindName)) return System.Linq.Enumerable.Empty<Edge>();
        if (!System.Enum.TryParse<EdgeKind>(kindName, ignoreCase: true, out var kind))
            return System.Linq.Enumerable.Empty<Edge>();
        return System.Linq.Enumerable.Where(Graph.Edges, e => e.Kind == kind);
    }
}
