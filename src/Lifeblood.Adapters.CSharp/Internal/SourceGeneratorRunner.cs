using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Runs source generators supplied by the target framework reference pack before
/// Lifeblood extracts symbols, edges, or diagnostics. This mirrors the subset of
/// MSBuild's compiler pipeline that produces syntax the user's code can depend
/// on, without adopting diagnostics-only analyzers into Lifeblood's analysis
/// surface.
/// </summary>
internal static class SourceGeneratorRunner
{
    public static CSharpCompilation Run(
        CSharpCompilation compilation,
        IReadOnlyList<string> analyzerPaths)
    {
        if (analyzerPaths.Count == 0)
            return compilation;

        var loader = new SourceGeneratorAnalyzerAssemblyLoader();
        var generators = new List<ISourceGenerator>();

        foreach (var path in analyzerPaths)
        {
            if (!File.Exists(path))
                continue;

            loader.AddDependencyLocation(path);
            try
            {
                var reference = new AnalyzerFileReference(path, loader);
                generators.AddRange(reference.GetGeneratorsForAllLanguages());
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
            {
                // Analyzer load failures should not make the whole workspace
                // unanalyzable. The compilation will surface any unresolved
                // generated members as diagnostics, exactly as pre-generator
                // Lifeblood did.
            }
        }

        if (generators.Count == 0)
            return compilation;

        var parseOptions = compilation.SyntaxTrees
            .Select(tree => tree.Options)
            .OfType<CSharpParseOptions>()
            .FirstOrDefault();
        var driver = CSharpGeneratorDriver.Create(
            generators,
            parseOptions: parseOptions);

        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updatedCompilation,
            out _);

        return (CSharpCompilation)updatedCompilation;
    }

    private sealed class SourceGeneratorAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencyLocation(string fullPath)
        {
            // AnalyzerFileReference calls this before LoadFromPath. The default
            // Assembly.LoadFrom probing is enough for framework-reference
            // generators because their dependencies are either next to the
            // analyzer or already loaded by Lifeblood's Roslyn host.
        }

        public Assembly LoadFromPath(string fullPath)
        {
            var normalized = Path.GetFullPath(fullPath);
            if (_loaded.TryGetValue(normalized, out var assembly))
                return assembly;

            assembly = Assembly.LoadFrom(normalized);
            _loaded[normalized] = assembly;
            return assembly;
        }
    }
}
