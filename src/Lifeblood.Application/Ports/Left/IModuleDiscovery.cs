namespace Lifeblood.Application.Ports.Left;

public interface IModuleDiscovery
{
    ModuleInfo[] DiscoverModules(string projectRoot);
}

/// <summary>
/// Where a module's base class library comes from. Decided at discovery time
/// from csproj inspection and consumed at compilation time. Adapter-agnostic.
///
/// See INV-BCL-001..INV-BCL-005 in <c>.claude/plans/bcl-ownership-fix.md</c>:
///   INV-BCL-001 — Single BCL per compilation. Two BCLs produces CS0433/CS0518
///                 errors on every System type, which silently breaks
///                 GetSymbolInfo at every call site, which silently returns
///                 zero results from find_references / dependants / call-graph
///                 extraction.
///   INV-BCL-002 — A module that ships its own BCL via csproj declaration MUST
///                 NOT also receive the host process BCL.
///   INV-BCL-003 — A plain SDK-style module without its own BCL MUST receive
///                 the host BCL so System types resolve.
///   INV-BCL-004 — The decision is computed once at discovery time and stored
///                 on ModuleInfo. Compilation reads the field, never re-derives.
/// </summary>
public enum BclOwnershipMode
{
    /// <summary>
    /// Module relies on the host process's runtime BCL (default for plain
    /// SDK-style .NET csprojs with no HintPath BCL refs). The compilation
    /// builder MUST inject the host BCL bundle.
    /// </summary>
    HostProvided = 0,

    /// <summary>
    /// Module ships its own BCL via csproj
    /// <c>&lt;Reference Include="netstandard|mscorlib|System.Runtime"&gt;</c>
    /// or via HintPath references resolving to those file names (Unity ships
    /// .NET Standard 2.1; .NET Framework / Mono / Xamarin ship mscorlib). The
    /// compilation builder MUST NOT inject the host BCL on top — doing so
    /// causes the silent semantic-model failure described in INV-BCL-001.
    /// </summary>
    ModuleProvided = 1,
}

/// <summary>
/// Whether the compilation reference set is the transitive closure of declared
/// dependencies, or strictly the direct dependency list. Decided at discovery
/// time from csproj inspection; consumed at compilation time. Adapter-agnostic.
///
/// See INV-MODULE-REFS-001 in <c>docs/invariants/module-refs.md</c>:
///   INV-MODULE-REFS-001 — Reference closure mirrors the build tool that owns the csproj.
///     SDK-style MSBuild closes ProjectReference transitively (the default
///     for modern .NET projects). Old-format MSBuild 2003-schema csprojs
///     (Unity asmdef generators) compile each module against direct
///     dependencies only; transitively-reachable assemblies are NEVER added
///     to the compile classpath. Lifeblood's compilation reference graph
///     MUST mirror the closure semantics of the source-of-truth build tool,
///     otherwise sibling-namespace assemblies become visible to lookup and
///     shadow BCL types (the canonical failure: <c>Math.Min</c> in
///     <c>namespace Acme.Foo</c> binds to <c>Acme.Math</c> namespace because
///     <c>Acme.Math.dll</c> was transitively pulled in even though the
///     module's asmdef does not declare a reference to it).
/// </summary>
public enum ReferenceClosureMode
{
    /// <summary>
    /// Full transitive closure of <see cref="ModuleInfo.Dependencies"/> is
    /// added to the compilation reference set. Mirrors SDK-style MSBuild,
    /// where compiling A pulls in every assembly reachable through A's
    /// ProjectReference graph so transitively-exposed types on B's public
    /// surface can bind in A's source. Default — preserves pre-fix behavior
    /// for SDK-style workspaces (Lifeblood self, NuGet ecosystem, modern .NET).
    /// </summary>
    Transitive = 0,

    /// <summary>
    /// Only the directly-declared dependencies (<see cref="ModuleInfo.Dependencies"/>
    /// as-is) become compilation references. Mirrors Unity asmdef compile
    /// semantics where each asmdef must explicitly list every assembly whose
    /// types appear in its source — transitively-reachable assemblies are
    /// NOT on the compile classpath. Set by <see cref="RoslynModuleDiscovery"/>
    /// when the csproj uses the old-format MSBuild 2003 schema (Unity's
    /// generator output). INV-MODULE-REFS-001.
    /// </summary>
    DirectOnly = 1,
}

public sealed class ModuleInfo
{
    public string Name { get; init; } = "";
    public string[] FilePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Names of the modules this one DIRECTLY depends on (populated from the
    /// csproj's own <c>&lt;ProjectReference&gt;</c> elements only). How this
    /// field expands into Roslyn compilation references depends on
    /// <see cref="ReferenceClosure"/>:
    /// <see cref="ReferenceClosureMode.Transitive"/> walks the full closure
    /// (SDK-style MSBuild); <see cref="ReferenceClosureMode.DirectOnly"/>
    /// uses this list as-is (Unity asmdef).
    ///
    /// Always direct-only when feeding module→module DependsOn graph edges
    /// or the topological compile sort — both want the user-declared shape,
    /// not the transitive closure. INV-MODULE-REFS-001 + INV-CANONICAL-001.
    /// </summary>
    public string[] Dependencies { get; init; } = Array.Empty<string>();

    public bool IsPure { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Absolute paths to external DLLs referenced via HintPath in the project file.
    /// These are non-NuGet, non-module DLLs (e.g., Unity engine assemblies) that Roslyn
    /// needs as metadata references for accurate compilation and diagnostics.
    /// </summary>
    public string[] ExternalDllPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Where this module's base class library comes from. Decided once at
    /// discovery time by inspecting the csproj's <c>&lt;Reference&gt;</c> elements;
    /// consumed by the compilation builder. Default <see cref="BclOwnershipMode.HostProvided"/>
    /// preserves pre-fix behavior for plain SDK-style csprojs and for any
    /// caller that constructs <see cref="ModuleInfo"/> without setting this
    /// field. See <see cref="BclOwnershipMode"/> for the full invariant set.
    /// </summary>
    public BclOwnershipMode BclOwnership { get; init; } = BclOwnershipMode.HostProvided;

    /// <summary>
    /// True iff the module's csproj declares
    /// <c>&lt;AllowUnsafeBlocks&gt;true&lt;/AllowUnsafeBlocks&gt;</c>. When true,
    /// the compilation builder MUST set
    /// <c>CSharpCompilationOptions.AllowUnsafe = true</c>; otherwise Roslyn
    /// emits false-positive CS0227 ("Unsafe code may only appear if compiling
    /// with /unsafe") on every <c>unsafe</c> block in the module, AND the
    /// semantic model goes silently null inside those blocks (find_references
    /// and edge extraction drop the affected symbols).
    ///
    /// Decided at discovery time by <see cref="RoslynModuleDiscovery"/>.
    /// Consumed at compilation time by <c>ModuleCompilationBuilder.CreateCompilation</c>.
    /// Default false preserves pre-fix behavior. See INV-COMPFACT-001..003 in CLAUDE.md.
    /// </summary>
    public bool AllowUnsafeCode { get; init; }

    /// <summary>
    /// True iff the module's csproj declares
    /// <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>. When true,
    /// the compilation builder MUST inject the standard global usings
    /// (<c>System</c>, <c>System.Collections.Generic</c>, <c>System.IO</c>,
    /// <c>System.Linq</c>, <c>System.Threading</c>, <c>System.Threading.Tasks</c>,
    /// <c>System.Net.Http</c>) as a synthetic syntax tree. Without these,
    /// Roslyn's <c>GetSymbolInfo</c> returns null for every invocation using
    /// types from the implicit namespaces — silently dropping 42% of call-graph
    /// edges (LB-INBOX-007).
    ///
    /// Decided at discovery time by <see cref="RoslynModuleDiscovery"/>.
    /// Consumed at compilation time by <c>ModuleCompilationBuilder.CreateCompilation</c>.
    /// Default false preserves pre-fix behavior. See INV-COMPFACT-001..003 in CLAUDE.md.
    /// </summary>
    public bool ImplicitUsings { get; init; }

    /// <summary>
    /// Preprocessor symbols declared by the csproj's
    /// <c>&lt;DefineConstants&gt;</c> property (split on <c>;</c>, trimmed,
    /// empties dropped). When non-empty, the compilation builder MUST thread
    /// these into <c>CSharpParseOptions.WithPreprocessorSymbols</c> on every
    /// syntax tree parsed for the module; otherwise every <c>#if</c>-guarded
    /// block whose token appears here is silently excluded from the
    /// compilation unit, and any symbol referenced only inside such a guard
    /// is invisible to <c>find_references</c> / <c>dead_code</c> /
    /// <c>blast_radius</c> (the empirical L-LIM-001 trap on any Unity-
    /// like workspace where production code wraps platform-specific
    /// callsites in <c>#if</c> guards).
    ///
    /// Decided at discovery time by <see cref="RoslynModuleDiscovery"/>.
    /// Consumed at compilation time by <c>ModuleCompilationBuilder.CreateCompilation</c>.
    /// Default empty preserves pre-fix behavior for csprojs that declare no
    /// <c>&lt;DefineConstants&gt;</c>. See INV-COMPFACT-001..003 in CLAUDE.md
    /// and INV-DIAGNOSTIC-ENVELOPE-DEFINES-001 / LB-TRACK-20260514-002.
    /// </summary>
    public string[] PreprocessorSymbols { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How <see cref="Dependencies"/> expands into Roslyn compilation
    /// references. Decided at discovery time by inspecting the csproj
    /// schema; consumed by <c>ModuleCompilationBuilder</c>. Default
    /// <see cref="ReferenceClosureMode.Transitive"/> preserves pre-fix
    /// behavior for SDK-style workspaces. See <see cref="ReferenceClosureMode"/>
    /// for the full invariant. INV-MODULE-REFS-001.
    /// </summary>
    public ReferenceClosureMode ReferenceClosure { get; init; } = ReferenceClosureMode.Transitive;
}
