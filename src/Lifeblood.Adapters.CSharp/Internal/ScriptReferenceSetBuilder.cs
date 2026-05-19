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
        var (bclRefs, targetWarnings) = ResolveTargetProfileBcl(targetProfile);

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

    /// <summary>
    /// Pick the BCL reference set for the requested target profile.
    /// Returns the references plus any non-fatal diagnostics
    /// (unknown profile, ref-pack not installed locally, etc.). Falls
    /// back to the host BCL on any miss so the script still has SOME
    /// reference set. See INV-EXECUTE-001.
    /// </summary>
    private static (MetadataReference[] refs, string[] warnings) ResolveTargetProfileBcl(string profile)
    {
        if (string.IsNullOrEmpty(profile) || string.Equals(profile, "host", StringComparison.OrdinalIgnoreCase))
            return (HostBclReferences.Value, Array.Empty<string>());

        var packPaths = TargetProfilePackCandidates(profile);
        if (packPaths.Count == 0)
        {
            return (HostBclReferences.Value, new[]
            {
                $"Unknown targetProfile '{profile}'. Falling back to host runtime BCL. " +
                "Supported values: 'host' (default), 'net-standard-2.1', 'net-6.0'.",
            });
        }

        foreach (var dir in packPaths)
        {
            if (!Directory.Exists(dir)) continue;
            var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
            if (dlls.Length == 0) continue;
            var refs = new List<MetadataReference>(dlls.Length);
            foreach (var d in dlls)
            {
                try { refs.Add(MetadataReference.CreateFromFile(d)); } catch { }
            }
            if (refs.Count > 0)
            {
                return (refs.ToArray(), new[] { $"Target profile '{profile}' resolved from {dir} ({refs.Count} assemblies)." });
            }
        }

        return (HostBclReferences.Value, new[]
        {
            $"Target profile '{profile}' selected but no matching reference pack was found on this machine. " +
            $"Searched: {string.Join(", ", packPaths)}. Falling back to host runtime BCL - " +
            "scripts may compile against APIs that are not present at the requested target.",
        });
    }

    private static List<string> TargetProfilePackCandidates(string profile)
    {
        var p = profile.ToLowerInvariant();
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        var list = new List<string>();
        switch (p)
        {
            case "net-standard-2.1":
            case "netstandard2.1":
                foreach (var sdk in EnumerateSdkPacks(pf, home))
                {
                    list.Add(Path.Combine(sdk, "NETStandard.Library.Ref", "2.1.0", "ref", "netstandard2.1"));
                }
                break;
            case "net-6.0":
            case "net6.0":
                foreach (var sdk in EnumerateSdkPacks(pf, home))
                {
                    var packBase = Path.Combine(sdk, "Microsoft.NETCore.App.Ref");
                    if (Directory.Exists(packBase))
                    {
                        foreach (var v in Directory.GetDirectories(packBase, "6.*").OrderByDescending(d => d))
                            list.Add(Path.Combine(v, "ref", "net6.0"));
                    }
                }
                break;
        }
        return list;
    }

    private static IEnumerable<string> EnumerateSdkPacks(string programFiles, string userHome)
    {
        yield return Path.Combine(programFiles, "dotnet", "packs");
        if (!string.IsNullOrEmpty(userHome))
            yield return Path.Combine(userHome, ".dotnet", "packs");
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
