using System.Reflection;
using System.Runtime.Loader;
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
    private static readonly Lock GeneratorDriverGate = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static CSharpCompilation Run(
        CSharpCompilation compilation,
        IReadOnlyList<string> analyzerPaths,
        ModuleInfo module,
        Scope? scope = null)
    {
        if (analyzerPaths.Count == 0)
            return compilation;

        lock (GeneratorDriverGate)
        {
            using var ownedScope = scope is null ? new Scope() : null;
            var loader = (scope ?? ownedScope!).GetLoader(analyzerPaths);
            var generators = new List<ISourceGenerator>();

            foreach (var path in analyzerPaths)
            {
                try
                {
                    var reference = new AnalyzerFileReference(path, loader);
                    reference.AnalyzerLoadFailed += (_, failure) => throw new InvalidOperationException(
                        $"Source generator load failed for module '{module.Name}' at '{path}': {failure.Message}",
                        failure.Exception);
                    generators.AddRange(reference.GetGeneratorsForAllLanguages());
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
                {
                    throw new InvalidOperationException(
                        $"Source generator load failed for module '{module.Name}' at '{path}'.", ex);
                }
            }

            if (generators.Count == 0)
                return compilation;

            var parseOptions = compilation.SyntaxTrees
                .Select(tree => tree.Options)
                .OfType<CSharpParseOptions>()
                .FirstOrDefault();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators,
                parseOptions: parseOptions,
                optionsProvider: BuildOptionsProvider(module));

            driver = driver.RunGeneratorsAndUpdateCompilation(
                compilation,
                out var updatedCompilation,
                out var diagnostics);

            var failures = driver.GetRunResult().Results.Where(result => result.Exception is not null).ToArray();
            if (failures.Length > 0 || diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException(
                    $"Source generator execution failed for module '{module.Name}': "
                    + string.Join("; ", diagnostics.Select(diagnostic => diagnostic.ToString()))
                    + string.Join("; ", failures.Select(result => result.Exception!.Message)));

            return (CSharpCompilation)updatedCompilation;
        }
    }

    /// <summary>
    /// Owns generator code for one compilation pass. Modules with the same
    /// analyzer inventory share loaded code while each run creates fresh
    /// generators and a driver. Different inventories retain separate type
    /// identities; no loader survives the pass in a process-wide cache.
    /// </summary>
    internal sealed class Scope : IDisposable
    {
        private readonly List<(string[] Paths, SourceGeneratorAnalyzerAssemblyLoader Loader)> _loaders = new();
        private bool _disposed;

        internal IAnalyzerAssemblyLoader GetLoader(IReadOnlyList<string> paths)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fullPaths = paths.Select(Path.GetFullPath).ToArray();
            foreach (var entry in _loaders)
                if (entry.Paths.SequenceEqual(fullPaths, PathComparer))
                    return entry.Loader;

            var loader = new SourceGeneratorAnalyzerAssemblyLoader(fullPaths);
            _loaders.Add((fullPaths, loader));
            return loader;
        }

        public void Dispose()
        {
            lock (GeneratorDriverGate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var entry in _loaders)
                    entry.Loader.Dispose();
                _loaders.Clear();
            }
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

    private sealed class SourceGeneratorAnalyzerAssemblyLoader : AssemblyLoadContext, IAnalyzerAssemblyLoader, IDisposable
    {
        private readonly Dictionary<string, string> _dependencies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Assembly> _loaded = new(PathComparer);

        public SourceGeneratorAnalyzerAssemblyLoader(IReadOnlyList<string> paths)
            : base(isCollectible: true)
        {
            foreach (var path in paths)
                AddDependencyLocation(path);
        }

        public void AddDependencyLocation(string fullPath)
        {
            var path = Path.GetFullPath(fullPath);
            _dependencies[Path.GetFileNameWithoutExtension(path)] = path;
        }

        public Assembly LoadFromPath(string fullPath)
        {
            var path = Path.GetFullPath(fullPath);
            if (_loaded.TryGetValue(path, out var loaded))
                return loaded;
            using var stream = File.OpenRead(path);
            var assembly = LoadFromStream(stream);
            _loaded.Add(path, assembly);
            return assembly;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Roslyn types cross the generator boundary, so they must retain
            // the host's type identity. Generator assemblies and private
            // dependencies belong to this inventory's compilation-pass scope.
            // They cannot contaminate another framework version or remain
            // rooted by a process-wide cache.
            foreach (var shared in new[] { typeof(Compilation).Assembly, typeof(CSharpCompilation).Assembly })
                if (string.Equals(shared.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                    return shared;

            if (assemblyName.Name is not null && _dependencies.TryGetValue(assemblyName.Name, out var registered))
                return LoadFromPath(registered);

            foreach (var directory in _dependencies.Values.Select(Path.GetDirectoryName).Distinct())
            {
                var candidate = Path.Combine(directory!, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromPath(candidate);
            }

            return null;
        }

        public void Dispose()
        {
            _loaded.Clear();
            _dependencies.Clear();
            Unload();
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
