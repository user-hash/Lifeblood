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

public sealed class ModuleInfo
{
    public string Name { get; init; } = "";
    public string[] FilePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Names of the modules this one DIRECTLY depends on (populated from the
    /// csproj's own <c>&lt;ProjectReference&gt;</c> elements only). These are
    /// NOT the Roslyn compilation references — Roslyn compilations need the
    /// full TRANSITIVE closure, because unlike MSBuild, Roslyn does not walk
    /// indirect references automatically. Consumers that build compilation
    /// reference lists MUST route this field through
    /// <c>ModuleCompilationBuilder.ComputeTransitiveDependencies</c>.
    ///
    /// Rationale and regression history: see INV-CANONICAL-001 in CLAUDE.md
    /// and <c>tests/Lifeblood.Tests/CanonicalSymbolFormatTests.cs</c>. The
    /// name of this field is kept as <c>Dependencies</c> rather than
    /// <c>DirectDependencies</c> because it also feeds module→module graph
    /// edges and the topological sort, both of which are correct with the
    /// direct-only semantics.
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
}
