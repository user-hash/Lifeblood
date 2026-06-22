using System.Reflection;
using Lifeblood.Application.Ports.Left;
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
    private static readonly object GeneratorDriverGate = new();

    public static CSharpCompilation Run(
        CSharpCompilation compilation,
        IReadOnlyList<string> analyzerPaths,
        ModuleInfo module)
    {
        if (analyzerPaths.Count == 0)
            return compilation;

        lock (GeneratorDriverGate)
        {
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
                parseOptions: parseOptions,
                optionsProvider: BuildOptionsProvider(module));

            driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var updatedCompilation,
                out _);

            return (CSharpCompilation)updatedCompilation;
        }
    }

    private static AnalyzerConfigOptionsProvider BuildOptionsProvider(ModuleInfo module)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(module.TargetFramework))
        {
            values["build_property.TargetFramework"] = module.TargetFramework;
            var match = System.Text.RegularExpressions.Regex.Match(module.TargetFramework, @"^net(\d+)\.(\d+)");
            if (match.Success)
            {
                values["build_property.TargetFrameworkIdentifier"] = ".NETCoreApp";
                values["build_property.TargetFrameworkVersion"] = $"v{match.Groups[1].Value}.{match.Groups[2].Value}";
                values["build_property.TargetFrameworkMoniker"] =
                    $".NETCoreApp,Version=v{match.Groups[1].Value}.{match.Groups[2].Value}";
            }
        }

        values["build_property.RootNamespace"] = module.Name;
        values["build_property.AssemblyName"] = module.Name;
        values["build_property.Nullable"] = string.IsNullOrWhiteSpace(module.NullableContext)
            ? "disable"
            : module.NullableContext;
        values["build_property.ImplicitUsings"] = module.ImplicitUsings ? "enable" : "disable";

        return new ModuleAnalyzerConfigOptionsProvider(values);
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

    private sealed class ModuleAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options;

        public ModuleAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> values)
        {
            _options = new ModuleAnalyzerConfigOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class ModuleAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public ModuleAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(
            string key,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            => _values.TryGetValue(key, out value);
    }
}
