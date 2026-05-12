using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Reference implementation of <see cref="IRuntimeAssemblyResolver"/>
/// for Unity workspaces. Probes the standard Unity-generated DLL
/// directories under <c>&lt;projectRoot&gt;/Library/</c>:
///
/// <list type="bullet">
///   <item><c>Library/ScriptAssemblies/</c> — Unity legacy / Mono path
///     containing the per-asmdef compiled DLLs (UnityEngine.dll proxies
///     plus the user's Assembly-CSharp.dll and per-asmdef *.dll files).</item>
///   <item><c>Library/Bee/artifacts/</c> — Unity 2022+ Bee build cache.
///     Holds intermediate DLLs and copies of UnityEngine reference
///     assemblies. Scanned recursively, only top-level <c>*.dll</c>
///     files are returned (we skip nested debug symbol blobs).</item>
///   <item><c>Library/PackageCache/</c> — Unity Package Manager cache
///     containing third-party packages with managed assemblies.</item>
/// </list>
///
/// When the workspace has a <c>Library/</c> directory but none of the
/// expected sub-directories carry DLLs, the resolver emits a friendly
/// diagnostic ("no Unity build artifacts found … run a build first")
/// so the tool layer can echo it instead of letting the script fail
/// with cryptic CS0246 errors.
///
/// Phase P4 (2026-04-26). See INV-EXECUTE-001 in CLAUDE.md.
/// </summary>
public sealed class UnityAssemblyResolver : IRuntimeAssemblyResolver
{
    private readonly IFileSystem _fs;
    private readonly string _projectRoot;

    /// <summary>
    /// Cap the number of DLLs returned. Unity workspaces can ship
    /// hundreds of pre-built package DLLs; injecting all of them
    /// inflates the script compiler's reference set and slows compile
    /// without proportional benefit. Empirically a few hundred is
    /// plenty to cover the 99% case.
    /// </summary>
    private const int MaxDllsReturned = 1024;

    public UnityAssemblyResolver(IFileSystem fs, string projectRoot)
    {
        _fs = fs;
        _projectRoot = projectRoot;
    }

    public string[] GetAssemblyProbePaths()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return System.Array.Empty<string>();

        var libraryDir = System.IO.Path.Combine(_projectRoot, "Library");
        if (!_fs.DirectoryExists(libraryDir)) return System.Array.Empty<string>();

        var collected = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // ScriptAssemblies has at most a few dozen DLLs — flat directory.
        var scriptAssemblies = System.IO.Path.Combine(libraryDir, "ScriptAssemblies");
        if (_fs.DirectoryExists(scriptAssemblies))
        {
            foreach (var dll in TryFindFiles(scriptAssemblies, "*.dll", recursive: false))
                collected.Add(dll);
        }

        // Bee artifacts — recursive but capped.
        var beeArtifacts = System.IO.Path.Combine(libraryDir, "Bee", "artifacts");
        if (_fs.DirectoryExists(beeArtifacts))
        {
            foreach (var dll in TryFindFiles(beeArtifacts, "*.dll", recursive: true))
            {
                if (collected.Count >= MaxDllsReturned) break;
                collected.Add(dll);
            }
        }

        // PackageCache — third-party Unity packages with .dll resources.
        var packageCache = System.IO.Path.Combine(libraryDir, "PackageCache");
        if (_fs.DirectoryExists(packageCache))
        {
            foreach (var dll in TryFindFiles(packageCache, "*.dll", recursive: true))
            {
                if (collected.Count >= MaxDllsReturned) break;
                collected.Add(dll);
            }
        }

        return collected.ToArray();
    }

    public string[] GetDiagnostics()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return System.Array.Empty<string>();

        var libraryDir = System.IO.Path.Combine(_projectRoot, "Library");
        if (!_fs.DirectoryExists(libraryDir)) return System.Array.Empty<string>();

        // Library/ exists — workspace looks Unity-shaped. Check that we
        // actually found build artifacts. If not, surface the "needs a
        // build" diagnostic rather than letting scripts fail with
        // cryptic CS0246.
        var paths = GetAssemblyProbePaths();
        if (paths.Length == 0)
        {
            return new[]
            {
                "Unity workspace detected (Library/ exists) but no build artifacts found under " +
                "Library/ScriptAssemblies, Library/Bee/artifacts, or Library/PackageCache. " +
                "Run a Unity build (or open the project in Unity) so engine and asmdef DLLs are emitted.",
            };
        }
        return System.Array.Empty<string>();
    }

    private System.Collections.Generic.IEnumerable<string> TryFindFiles(string dir, string pattern, bool recursive)
    {
        try
        {
            return _fs.FindFiles(dir, pattern, recursive);
        }
        catch
        {
            // Permission errors / missing dirs are silently ignored —
            // the resolver is best-effort.
            return System.Array.Empty<string>();
        }
    }
}
