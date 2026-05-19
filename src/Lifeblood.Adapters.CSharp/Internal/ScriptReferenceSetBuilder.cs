using Lifeblood.Application.Ports.Left;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Builds the Roslyn scripting reference set for <c>lifeblood_execute</c>.
/// This is deliberately separate from <see cref="BclReferenceLoader"/>:
/// normal module compilations prefer SDK reference assemblies, while
/// <see cref="Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript"/>
/// needs the host implementation BCL that carries the real core library.
///
/// The builder is the one place where execute-time references are admitted:
/// host BCL, retained workspace compilations, optional runtime probe DLLs,
/// and the assemblies that define the script globals. Keeping that policy
/// here prevents each caller from rediscovering native-PE filtering,
/// Unity stripped-BCL exclusion, and duplicate identity handling.
/// </summary>
internal sealed class ScriptReferenceSetBuilder
{
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private readonly IRuntimeAssemblyResolver? _runtimeAssemblies;

    /// <summary>
    /// Host BCL assemblies, resolved once from the running .NET runtime
    /// directory. ScriptOptions.Default only has "Unresolved" assembly
    /// names, so execute supplies explicit file-backed references.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> HostBclReferences = new(LoadHostBclReferences);

    public ScriptReferenceSetBuilder(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        IRuntimeAssemblyResolver? runtimeAssemblies)
    {
        _compilations = compilations;
        _runtimeAssemblies = runtimeAssemblies;
    }

    public ScriptReferenceSet Build(string targetProfile)
    {
        var runtimeAssemblyWarnings = _runtimeAssemblies?.GetDiagnostics() ?? Array.Empty<string>();
        var targetWarnings = ResolveTargetProfileWarnings(targetProfile);
        var bclRefs = HostBclReferences.Value;

        var references = new List<MetadataReference>(bclRefs.Length + _compilations.Count + 8);
        references.AddRange(bclRefs);

        var workspaceAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in _compilations.Values)
        {
            if (!string.IsNullOrEmpty(compilation.AssemblyName))
                workspaceAssemblyNames.Add(compilation.AssemblyName);
            references.Add(compilation.ToMetadataReference());
        }

        if (_runtimeAssemblies != null)
        {
            AddRuntimeProbeReferences(references, workspaceAssemblyNames, ref runtimeAssemblyWarnings);
        }

        AddScriptGlobalsReferences(references);

        return new ScriptReferenceSet
        {
            References = references.ToArray(),
            RuntimeAssemblyWarnings = runtimeAssemblyWarnings,
            TargetRuntimeWarnings = targetWarnings,
        };
    }

    private void AddRuntimeProbeReferences(
        List<MetadataReference> references,
        HashSet<string> workspaceAssemblyNames,
        ref string[] runtimeAssemblyWarnings)
    {
        var seenRuntimeIdentities = new HashSet<string>(StringComparer.Ordinal);
        var skippedNativeNames = new List<string>();
        var skippedBclNames = new List<string>();

        foreach (var path in OrderedRuntimeProbePaths())
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (!File.Exists(path)) continue;

            System.Reflection.AssemblyName identity;
            try
            {
                identity = System.Reflection.AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                skippedNativeNames.Add(Path.GetFileName(path));
                continue;
            }
            catch
            {
                continue;
            }

            var simpleName = identity.Name ?? "";
            if (IsRuntimeBclAssembly(simpleName))
            {
                skippedBclNames.Add(Path.GetFileName(path));
                continue;
            }

            if (workspaceAssemblyNames.Contains(simpleName))
            {
                continue;
            }

            if (!seenRuntimeIdentities.Add(IdentityKey(identity)))
            {
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch
            {
                // AssemblyName already filters native PEs, but Roslyn can
                // still reject malformed metadata. Runtime probing is
                // best-effort; skip inaccessible candidates.
            }
        }

        runtimeAssemblyWarnings = AppendSkipDiagnostic(
            runtimeAssemblyWarnings,
            skippedNativeNames,
            "non-managed PE(s)",
            "native PEs are not valid Roslyn metadata references");
        runtimeAssemblyWarnings = AppendSkipDiagnostic(
            runtimeAssemblyWarnings,
            skippedBclNames,
            "runtime BCL/contract assembly candidate(s)",
            "execute uses the host scripting BCL");
    }

    private IEnumerable<string> OrderedRuntimeProbePaths()
    {
        return _runtimeAssemblies!
            .GetAssemblyProbePaths()
            .OrderBy(RuntimeProbePriority)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static int RuntimeProbePriority(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase)) return 0;
        if (normalized.Contains("/Library/PackageCache/", StringComparison.OrdinalIgnoreCase)) return 1;
        if (normalized.Contains("/Library/Bee/artifacts/", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static void AddScriptGlobalsReferences(List<MetadataReference> references)
    {
        references.Add(MetadataReference.CreateFromFile(typeof(RoslynSemanticView).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Lifeblood.Domain.Graph.SemanticGraph).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location));
    }

    private static bool IsRuntimeBclAssembly(string simpleName)
    {
        if (simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.Equals("System", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return true;
        if (simpleName.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string IdentityKey(System.Reflection.AssemblyName identity)
    {
        var token = identity.GetPublicKeyToken();
        var tokenText = token is { Length: > 0 }
            ? Convert.ToHexString(token)
            : "";
        return string.Concat(
            (identity.Name ?? "").ToLowerInvariant(),
            "|",
            (identity.CultureName ?? "").ToLowerInvariant(),
            "|",
            tokenText);
    }

    private static string[] AppendSkipDiagnostic(
        string[] existing,
        List<string> skippedNames,
        string category,
        string reason)
    {
        if (skippedNames.Count == 0) return existing;

        var preview = string.Join(", ", skippedNames.Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        var suffix = skippedNames.Count > 3
            ? $" (+{skippedNames.Count - 3} more)"
            : "";
        var diagnostic = $"Skipped {skippedNames.Count} {category} from runtime probe: {preview}{suffix}; {reason}.";
        var combined = new string[existing.Length + 1];
        Array.Copy(existing, combined, existing.Length);
        combined[existing.Length] = diagnostic;
        return combined;
    }

    private static string[] ResolveTargetProfileWarnings(string profile)
    {
        if (string.IsNullOrEmpty(profile) || string.Equals(profile, "host", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        return new[]
        {
            $"targetProfile '{profile}' is informational for lifeblood_execute. " +
            "Scripts run in-process against the host scripting BCL so they can share the loaded Roslyn workspace state. " +
            "Non-host ref-pack surface validation is not implemented by lifeblood_execute.",
        };
    }

    private static MetadataReference[] LoadHostBclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null)
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        var coreAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Console.dll",
            "System.Collections.dll",
            "System.Collections.Concurrent.dll",
            "System.Linq.dll",
            "System.Linq.Expressions.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Text.RegularExpressions.dll",
            "System.Memory.dll",
            "System.IO.dll",
            "System.IO.FileSystem.dll",
            "System.Diagnostics.Debug.dll",
            "System.Runtime.Extensions.dll",
            "System.Runtime.InteropServices.dll",
            "System.ComponentModel.dll",
            "System.ObjectModel.dll",
            "netstandard.dll",
        };

        foreach (var dll in coreAssemblies)
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        return refs.ToArray();
    }
}

internal sealed class ScriptReferenceSet
{
    public required MetadataReference[] References { get; init; }
    public required string[] RuntimeAssemblyWarnings { get; init; }
    public required string[] TargetRuntimeWarnings { get; init; }
}
