using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Builds CSharpCompilations for modules in dependency order.
/// Handles topological sorting, BCL references, NuGet resolution, and cross-module linking.
/// </summary>
internal sealed class ModuleCompilationBuilder
{
    private readonly IFileSystem _fs;
    private readonly NuGetReferenceResolver _nuget;

    public ModuleCompilationBuilder(IFileSystem fs)
    {
        _fs = fs;
        _nuget = new NuGetReferenceResolver(fs);
    }

    /// <summary>
    /// Build compilations for all modules in dependency order.
    /// Returns a dictionary keyed by module (assembly) name.
    /// </summary>
    public Dictionary<string, CSharpCompilation> BuildAll(
        ModuleInfo[] modules, string projectRoot, AnalysisConfig config)
    {
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        var sorted = TopologicalSort(modules);

        foreach (var module in sorted)
        {
            var depCompilations = module.Dependencies
                .Where(compilations.ContainsKey)
                .Select(d => compilations[d])
                .ToArray();

            var compilation = CreateCompilation(module, projectRoot, config, depCompilations);
            if (compilation != null)
                compilations[module.Name] = compilation;
        }

        return compilations;
    }

    private CSharpCompilation? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config,
        CSharpCompilation[] dependencyCompilations)
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
        references.AddRange(_nuget.Resolve(module, projectRoot));

        foreach (var dep in dependencyCompilations)
            references.Add(dep.ToMetadataReference());

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
