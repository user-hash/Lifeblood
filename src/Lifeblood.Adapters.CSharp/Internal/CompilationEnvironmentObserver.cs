using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>Projects existing compiler objects without binding references or creating another compilation.</summary>
internal static class CompilationEnvironmentObserver
{
    internal static CompilationEnvironmentReport Observe(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        CompilationEnvironmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReferenceLimit is < 0 or > CompilationEnvironmentRequest.MaximumReferences)
            throw new ArgumentOutOfRangeException(nameof(request.ReferenceLimit));
        if (string.IsNullOrWhiteSpace(request.FilePath) && string.IsNullOrWhiteSpace(request.ModuleName))
            throw new ArgumentException("filePath or moduleName is required for an environment observation.");

        var ownership = new CompilationFileOwnership();
        var module = request.ModuleName ?? "";
        CSharpCompilation? compilation;
        SyntaxTree? tree = null;
        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            var owner = CompilationFileOwnershipResolver.Resolve(compilations, request.FilePath, request.ModuleName);
            ownership = owner.Ownership;
            if (ownership.Outcome != CompilationFileOwnershipOutcome.Unique)
                return new() { Status = "FileOwnershipUnresolved", FileOwnership = ownership };
            module = ownership.ResolvedModule;
            compilation = owner.Compilation;
            tree = owner.Tree;
        }
        else if (!compilations.TryGetValue(module, out compilation))
            return new() { Status = "ModuleNotFound", ModuleName = module };

        var options = compilation!.Options;
        IEnumerable<SyntaxTree> trees = tree == null ? compilation.SyntaxTrees : new[] { tree };
        var groups = trees
            .GroupBy(source => (CSharpParseOptions)source.Options)
            .ToArray();
        var references = compilation.References.ToArray();
        var compiler = typeof(CSharpCompilation).Assembly;
        return new()
        {
            Status = "Available",
            ModuleName = module,
            FileOwnership = ownership,
            Compiler = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = compiler.GetName().Name ?? "",
                ["version"] = compiler.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? compiler.GetName().Version?.ToString() ?? "",
                ["hostRuntime"] = RuntimeInformation.FrameworkDescription,
                ["hostArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            },
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["assemblyName"] = compilation.AssemblyName ?? "",
                ["allowUnsafe"] = options.AllowUnsafe.ToString(),
                ["nullableContext"] = options.NullableContextOptions.ToString(),
                ["optimizationLevel"] = options.OptimizationLevel.ToString(),
                ["platform"] = options.Platform.ToString(),
                ["outputKind"] = options.OutputKind.ToString(),
                ["checkOverflow"] = options.CheckOverflow.ToString(),
                ["warningLevel"] = options.WarningLevel.ToString(CultureInfo.InvariantCulture),
                ["generalDiagnosticOption"] = options.GeneralDiagnosticOption.ToString(),
                ["deterministic"] = options.Deterministic.ToString(),
            },
            ParseEnvironmentCount = groups.Length,
            ParseEnvironments = groups.Take(8).Select(group => ParseEnvironment(group.Key, group.Count(), request.IncludeDefines)).ToArray(),
            ReferenceCount = references.Length,
            References = references.Take(request.ReferenceLimit).Select(reference => new CompilationReferenceEnvironment
            {
                Kind = reference is CompilationReference ? "compilation"
                    : reference is PortableExecutableReference { FilePath: not null } ? "file" : "inMemoryMetadata",
                FilePath = (reference as PortableExecutableReference)?.FilePath,
                Display = reference.Display,
                EmbedInteropTypes = reference.Properties.EmbedInteropTypes,
                AliasCount = reference.Properties.Aliases.Length,
                Aliases = reference.Properties.Aliases.Take(16).ToArray(),
            }).ToArray(),
            DiagnosticOptionCount = options.SpecificDiagnosticOptions.Count,
            DiagnosticOptions = options.SpecificDiagnosticOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(128)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal),
        };
    }

    private static CompilationParseEnvironment ParseEnvironment(CSharpParseOptions options, int treeCount, bool includeDefines)
    {
        var defines = options.PreprocessorSymbolNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new()
        {
            SourceTreeCount = treeCount,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["language"] = options.Language,
                ["languageVersion"] = options.LanguageVersion.ToDisplayString(),
                ["specifiedLanguageVersion"] = options.SpecifiedLanguageVersion.ToDisplayString(),
                ["documentationMode"] = options.DocumentationMode.ToString(),
                ["sourceCodeKind"] = options.Kind.ToString(),
            },
            DefinesActiveCount = defines.Length,
            DefinesActive = includeDefines ? defines.Take(256).ToArray() : Array.Empty<string>(),
            FeatureCount = options.Features.Count,
            Features = options.Features.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(32)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }
}
