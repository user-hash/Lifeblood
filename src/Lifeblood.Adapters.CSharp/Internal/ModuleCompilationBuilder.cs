using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
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
        Action<string, int, int>? onModuleProgress = null)
    {
        var sorted = TopologicalSort(modules);

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

            // Collect dependencies: use downgraded refs (lightweight) for completed modules.
            var depRefs = module.Dependencies
                .Where(downgraded.ContainsKey)
                .Select(d => downgraded[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depRefs);
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
        MetadataReference[] dependencyRefs)
    {
        var sourceFiles = module.FilePaths
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(_fs.FileExists);

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
            .ToArray();

        if (trees.Length == 0) return null;

        var references = new List<MetadataReference>(BclReferenceLoader.References.Value);
        references.AddRange(_nuget.Resolve(module, projectRoot, _refCache));
        references.AddRange(dependencyRefs);

        return CSharpCompilation.Create(
            module.Name,
            trees!,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
