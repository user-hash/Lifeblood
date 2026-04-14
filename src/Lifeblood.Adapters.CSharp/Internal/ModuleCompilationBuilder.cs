using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Builds CSharpCompilations for modules in dependency order.
/// Supports two modes:
///   Streaming (default): compile → extract → downgrade → next module. O(1) compilation memory.
///   Retained: compile → extract → keep in memory for write-side tools. O(N) compilation memory.
///
/// Compilation downgrading: after extraction, Emit() to a byte[] and create a lightweight
/// MetadataReference.CreateFromImage(). Downstream modules reference the PE image (~10-100KB)
/// instead of the full compilation (~200MB). The full compilation becomes eligible for GC.
/// </summary>
internal sealed class ModuleCompilationBuilder
{
    private readonly IFileSystem _fs;
    private readonly NuGetReferenceResolver _nuget;
    private readonly SharedMetadataReferenceCache _refCache;

    /// <summary>
    /// Synthetic tree matching the global usings MSBuild generates when
    /// <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c> is set.
    /// Parsed once, shared across all compilations that need it.
    /// </summary>
    private static readonly SyntaxTree ImplicitGlobalUsings = CSharpSyntaxTree.ParseText(
        """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """,
        path: "<ImplicitGlobalUsings>.cs");

    public ModuleCompilationBuilder(IFileSystem fs, SharedMetadataReferenceCache? refCache = null)
    {
        _fs = fs;
        _nuget = new NuGetReferenceResolver(fs);
        _refCache = refCache ?? new SharedMetadataReferenceCache();
    }

    /// <summary>
    /// Callback invoked for each module after its compilation is built.
    /// The compilation is valid only during the callback — after return,
    /// it may be downgraded to a lightweight metadata reference.
    /// </summary>
    internal delegate void CompilationProcessor(
        ModuleInfo module, CSharpCompilation compilation);

    /// <summary>
    /// Process modules in dependency order, one at a time.
    /// For each module: build compilation → invoke processor → downgrade or retain.
    /// </summary>
    /// <param name="modules">Discovered modules from IModuleDiscovery.</param>
    /// <param name="projectRoot">Workspace root path.</param>
    /// <param name="config">Analysis configuration (exclude patterns, retention flag).</param>
    /// <param name="processor">Called with each compilation for symbol/edge extraction.</param>
    /// <returns>
    /// Retained compilations if config.RetainCompilations is true; null otherwise.
    /// When null, compilations have been downgraded — only the graph survives.
    /// </returns>
    public Dictionary<string, CSharpCompilation>? ProcessInOrder(
        ModuleInfo[] modules,
        string projectRoot,
        AnalysisConfig config,
        CompilationProcessor processor,
        Action<string, int, int>? onModuleProgress = null,
        List<SkippedFile>? skippedCollector = null)
    {
        var sorted = TopologicalSort(modules);
        var moduleLookup = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);

        // Downgraded references: lightweight PE images for completed modules.
        // Downstream modules reference these instead of full compilations.
        var downgraded = new Dictionary<string, MetadataReference>(StringComparer.Ordinal);

        // Only allocated when retaining for write-side tools.
        Dictionary<string, CSharpCompilation>? retained = config.RetainCompilations
            ? new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            : null;

        for (int i = 0; i < sorted.Length; i++)
        {
            var module = sorted[i];
            onModuleProgress?.Invoke(module.Name, i + 1, sorted.Length);

            // Collect dependencies transitively. Roslyn compilation references
            // are NOT transitive the way MSBuild project references are: if
            // module A references B, and B references C, compiling A needs BOTH
            // B and C as explicit references or Roslyn cannot bind any type
            // from C used through B's public surface — the type becomes an
            // error symbol with an empty ContainingNamespace, and every method
            // symbol ID built from it loses its namespace qualifier, silently
            // producing non-canonical IDs like `method:...Foo.Resolve(SemanticGraph,string)`
            // instead of the fully-qualified `(Lifeblood.Domain.Graph.SemanticGraph,string)`.
            // The topological sort above guarantees that every transitive
            // dependency of `module` has already been compiled and downgraded,
            // so the closure we compute here is always fully satisfiable.
            // Root cause and dogfood evidence in
            // .claude/plans/improvement-master-2026-04-11.md Part 1 NEW-01.
            var depRefs = ComputeTransitiveDependencies(module, moduleLookup)
                .Where(downgraded.ContainsKey)
                .Select(d => downgraded[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depRefs, skippedCollector);
            if (compilation == null) continue;

            // Invoke the processor (symbol/edge extraction happens here).
            processor(module, compilation);

            // Retain full compilation if write-side tools are needed.
            if (retained != null)
                retained[module.Name] = compilation;

            // Downgrade: emit to PE bytes → lightweight MetadataReference.
            // Downstream modules only need the type metadata, not the full compilation.
            var downgradedRef = DowngradeCompilation(compilation);
            downgraded[module.Name] = downgradedRef;
        }

        return retained;
    }

    /// <summary>
    /// Emit the compilation to a PE image and wrap it as a MetadataReference.
    /// Falls back to ToMetadataReference() if emit fails (compilation errors).
    /// The fallback keeps the full compilation alive — acceptable for the few
    /// modules that have errors, while most modules get the memory savings.
    /// </summary>
    private static MetadataReference DowngradeCompilation(CSharpCompilation compilation)
    {
        try
        {
            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (emitResult.Success)
                return MetadataReference.CreateFromImage(ms.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BadImageFormatException)
        {
            // Emit can throw for pathological compilations. Fall back gracefully.
        }

        // Fallback: wrap the in-memory compilation. More expensive but correct.
        return compilation.ToMetadataReference();
    }

    private CSharpCompilation? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config,
        MetadataReference[] dependencyRefs,
        List<SkippedFile>? skippedCollector)
    {
        // Surface every file the adapter declines to process so consumers
        // can show users exactly what was dropped and why. Phase 4 / C4.
        // Order matters: extension filter runs first, then file-existence,
        // so a missing non-.cs file is reported as UnsupportedExtension
        // (the dominant reason) rather than FileNotFound.
        var sourceFiles = module.FilePaths
            .Where(f =>
            {
                if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    skippedCollector?.Add(new SkippedFile
                    {
                        FilePath = f,
                        Reason = SkipReason.UnsupportedExtension,
                        ModuleName = module.Name,
                    });
                    return false;
                }
                return true;
            })
            .Where(f =>
            {
                if (!_fs.FileExists(f))
                {
                    skippedCollector?.Add(new SkippedFile
                    {
                        FilePath = f,
                        Reason = SkipReason.FileNotFound,
                        ModuleName = module.Name,
                    });
                    return false;
                }
                return true;
            });

        if (config.ExcludePatterns.Length > 0)
        {
            sourceFiles = sourceFiles.Where(f =>
            {
                var rel = Path.GetRelativePath(projectRoot, f).Replace('\\', '/');
                return !config.ExcludePatterns.Any(p => rel.Contains(p, StringComparison.OrdinalIgnoreCase));
            });
        }

        var trees = sourceFiles
            .Select(f =>
            {
                try { return CSharpSyntaxTree.ParseText(_fs.ReadAllText(f), path: f); }
                catch (IOException) { return null; }
            })
            .Where(t => t != null)
            .Select(t => t!)  // non-null proven by the Where above; strips the nullable flow annotation so downstream sees SyntaxTree[], not SyntaxTree?[]
            .ToArray();

        if (trees.Length == 0) return null;

        // BCL ownership is a discovered module fact. Read the field, act on it.
        // Do not re-derive from ExternalDllPaths, do not sniff filenames here —
        // detection lives in RoslynModuleDiscovery, single source of truth.
        // See INV-BCL-001..INV-BCL-004 in .claude/plans/bcl-ownership-fix.md
        // for the failure mode this prevents (CS0433/CS0518 → null GetSymbolInfo
        // → silent zero results from find_references / dependants / call-graph).
        var references = module.BclOwnership == BclOwnershipMode.ModuleProvided
            ? new List<MetadataReference>()
            : new List<MetadataReference>(BclReferenceLoader.References.Value);
        references.AddRange(_nuget.Resolve(module, projectRoot, _refCache));
        references.AddRange(dependencyRefs);

        // Load external DLLs referenced via HintPath (e.g., Unity engine assemblies).
        // Uses the shared cache to deduplicate — 100 modules sharing UnityEngine.CoreModule.dll
        // produce a single MetadataReference, not 100 independent copies.
        foreach (var dllPath in module.ExternalDllPaths)
        {
            try
            {
                if (!BclReferenceLoader.IsNativeDll(dllPath))
                    references.Add(_refCache.GetOrCreate(dllPath));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { }
        }

        // Compilation options follow discovered module facts (INV-COMPFACT-001..003).
        // Each csproj-driven option lives as a typed field on ModuleInfo, set
        // once during discovery, consumed exactly once here. NEVER re-derive
        // from the csproj at this layer; NEVER sniff filenames as a substitute.
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(module.AllowUnsafeCode);

        // Implicit global usings (INV-COMPFACT-001..003). When the csproj declares
        // <ImplicitUsings>enable</ImplicitUsings>, MSBuild generates global usings
        // for the standard namespaces. We inject the same set as a synthetic tree
        // because we compile from source, not through MSBuild.
        var allTrees = module.ImplicitUsings
            ? trees.Append(ImplicitGlobalUsings).ToArray()
            : trees;

        return CSharpCompilation.Create(
            module.Name,
            allTrees,
            references,
            compilationOptions);
    }

    /// <summary>
    /// Compute the transitive closure of module dependencies. Given module A
    /// with direct Dependencies [B], and B with direct Dependencies [C],
    /// returns {B, C}. Direct-only dependency lists are NOT sufficient for
    /// Roslyn compilation references: Roslyn needs every assembly whose types
    /// appear in the module's source, including assemblies reached transitively
    /// through another reference's public surface. Missing transitive refs
    /// silently produce error type symbols with empty namespaces, which in
    /// turn produce non-canonical symbol IDs during extraction.
    ///
    /// Cycles are handled gracefully — every visited module is added once and
    /// not revisited. Modules missing from the lookup (malformed dependency
    /// names) are silently skipped. The order of the returned set is not
    /// significant; the caller filters it against the `downgraded` map.
    /// </summary>
    internal static HashSet<string> ComputeTransitiveDependencies(
        ModuleInfo module,
        Dictionary<string, ModuleInfo> lookup)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(module.Dependencies);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (!closure.Add(name)) continue;
            if (!lookup.TryGetValue(name, out var dep)) continue;
            foreach (var transitive in dep.Dependencies)
            {
                if (!closure.Contains(transitive))
                    stack.Push(transitive);
            }
        }
        return closure;
    }

    /// <summary>
    /// Topological sort: returns modules in dependency-first order.
    /// Handles cycles gracefully — breaks them by processing what's available.
    /// </summary>
    internal static ModuleInfo[] TopologicalSort(ModuleInfo[] modules)
    {
        var lookup = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal);
        var result = new List<ModuleInfo>();

        foreach (var module in modules)
        {
            if (!visited.ContainsKey(module.Name))
                Visit(module, lookup, visited, result);
        }

        return result.ToArray();
    }

    private static void Visit(
        ModuleInfo module,
        Dictionary<string, ModuleInfo> lookup,
        Dictionary<string, bool> visited,
        List<ModuleInfo> result)
    {
        if (visited.TryGetValue(module.Name, out var permanent))
        {
            if (permanent) return;
            return; // cycle detected — break by skipping
        }

        visited[module.Name] = false;

        foreach (var dep in module.Dependencies)
        {
            if (lookup.TryGetValue(dep, out var depModule))
                Visit(depModule, lookup, visited, result);
        }

        visited[module.Name] = true;
        result.Add(module);
    }
}
