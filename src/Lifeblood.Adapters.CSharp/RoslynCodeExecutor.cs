using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Executes C# code via Roslyn scripting against a loaded workspace.
/// Inspired by Unity MCP's ExecuteCode.cs but using CSharpScript directly
/// (no reflection dance — we have Roslyn as a compile-time dependency).
/// </summary>
public sealed class RoslynCodeExecutor : ICodeExecutor
{
    private readonly RoslynSemanticView _view;
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private readonly IRuntimeAssemblyResolver? _runtimeAssemblies;

    private static readonly HashSet<string> BlockedPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // File write/mutate operations
        "System.IO.File.Delete",
        "System.IO.File.WriteAllText",
        "System.IO.File.WriteAllBytes",
        "System.IO.File.WriteAllLines",
        "System.IO.File.AppendAllText",
        "System.IO.File.AppendAllLines",
        "System.IO.File.Create",
        "System.IO.File.Move",
        "System.IO.File.Copy",
        "System.IO.File.SetAttributes",
        // Directory mutate operations
        "System.IO.Directory.Delete",
        "System.IO.Directory.CreateDirectory",
        "System.IO.Directory.Move",
        // Instance method bypass — FileInfo/DirectoryInfo skip static File/Directory checks
        "new FileInfo",
        "new DirectoryInfo",
        // Stream writers (can write to arbitrary paths)
        "new StreamWriter",
        "new FileStream",
        // Process operations
        "Process.Start",
        "Process.Kill",
        "new Process",           // prevents var p = new Process(); p.Start() bypass
        "new ProcessStartInfo",  // prevents implicit Process.Start via StartInfo
        // Environment
        "Environment.Exit",
        "Environment.SetEnvironmentVariable",
        // Assembly loading
        "Assembly.Load",
        "Assembly.LoadFile",
        "Assembly.LoadFrom",
        "Assembly.UnsafeLoadFrom",
        // IL generation
        "Reflection.Emit",
        "AssemblyBuilder",
        // Expression tree compilation — produces unblockable delegates
        ".Compile()",
        // Thread operations
        "Thread.Abort",
        // P/Invoke — native code execution
        "DllImport",
        "Marshal.Copy",
        "Marshal.PtrToStructure",
        // Network
        "new HttpClient",
        "new TcpClient",
        "new Socket",
        "WebRequest.Create",
    };

    private static readonly string[] DefaultImports =
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",
        "System.IO",
    };

    /// <summary>
    /// Host BCL assemblies, resolved once from the running .NET runtime directory.
    /// ScriptOptions.Default only has "Unresolved" assembly names — these are the
    /// real, file-backed references the script compiler actually needs.
    /// </summary>
    private static readonly Lazy<MetadataReference[]> HostBclReferences = new(LoadHostBclReferences);

    /// <summary>
    /// Construct an executor over a typed read-only view of the loaded
    /// semantic state. Plan v4 Seam #3 — the script-host globals object IS
    /// the same <see cref="RoslynSemanticView"/> instance, passed via
    /// <c>CSharpScript.RunAsync&lt;RoslynSemanticView&gt;</c>. Scripts reach
    /// the loaded state via top-level identifiers <c>Graph</c>,
    /// <c>Compilations</c>, <c>ModuleDependencies</c>. See INV-VIEW-001..003.
    /// </summary>
    public RoslynCodeExecutor(RoslynSemanticView view)
    {
        _view = view;
        _compilations = view.Compilations;
    }

    /// <summary>
    /// Construct an executor with an additional runtime-assembly resolver
    /// (Unity build artifacts, ASP.NET runtime pack, etc.). The resolver's
    /// probe paths are added to the script compiler's reference list so
    /// scripts can touch types that live outside the analyzed source.
    /// </summary>
    public RoslynCodeExecutor(RoslynSemanticView view, IRuntimeAssemblyResolver? runtimeAssemblies)
    {
        _view = view;
        _compilations = view.Compilations;
        _runtimeAssemblies = runtimeAssemblies;
    }

    /// <summary>
    /// Backward-compatible constructor for tests and standalone callers that
    /// only have a compilations dictionary. Wraps the dictionary in a minimal
    /// <see cref="RoslynSemanticView"/> with an empty graph and empty
    /// dependency map. Scripts run via this overload won't be able to query
    /// the graph or module dependencies — they should use the view-aware
    /// constructor for full functionality.
    /// </summary>
    public RoslynCodeExecutor(IReadOnlyDictionary<string, CSharpCompilation> compilations)
        : this(new RoslynSemanticView(
            compilations,
            new Lifeblood.Domain.Graph.SemanticGraph(),
            new Dictionary<string, string[]>(StringComparer.Ordinal)))
    {
    }

    public CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000)
        => Execute(new CodeExecutionRequest
        {
            Code = code,
            Imports = imports,
            TimeoutMs = timeoutMs,
            TargetProfile = "host",
        });

    public CodeExecutionResult Execute(CodeExecutionRequest request)
    {
        var code = request.Code;
        var imports = request.Imports;
        var timeoutMs = request.TimeoutMs;
        var startTime = DateTime.UtcNow;

        // Resolver diagnostics computed up-front so they're surfaced on
        // every result path (success, compilation error, blocked pattern,
        // exception) — even when the script never actually runs.
        var runtimeAssemblyDiagnostics = _runtimeAssemblies?.GetDiagnostics() ?? Array.Empty<string>();
        var (bclRefs, targetWarnings) = ResolveTargetProfileBcl(request.TargetProfile);

        // Layer 1: String-based blocklist (fast, catches obvious patterns).
        // Normalize whitespace around dots to prevent bypass via "Process . Start".
        // C# allows arbitrary whitespace between member-access tokens.
        var normalizedCode = NormalizeMemberAccess(code);
        foreach (var pattern in BlockedPatterns)
        {
            if (normalizedCode.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = $"Blocked pattern detected: {pattern}",
                    ElapsedMs = 0,
                    RuntimeAssemblyWarnings = runtimeAssemblyDiagnostics,
                    TargetRuntimeWarnings = targetWarnings,
                };
        }

        // Layer 2: AST-based security scan (catches reflection, dynamic, unsafe)
        var astBlock = ScriptSecurityScanner.Scan(code);
        if (astBlock != null)
            return new CodeExecutionResult
            {
                Success = false,
                Error = astBlock,
                ElapsedMs = 0,
            };

        try
        {
            // Build references from two sources:
            //  1. Host BCL — the running .NET runtime's actual assemblies (System.Runtime,
            //     System.Linq, etc.). NOT the target project's BCL, which may be a different
            //     runtime (e.g., Unity's netstandard stubs) and causes CS0518 conflicts.
            //  2. CompilationReferences — give the script access to project types.
            //     NOT compilation.References (transitive deps), which would inject the
            //     target project's BCL assemblies and conflict with the host BCL.
            //
            // We use WithReferences (replace) instead of AddReferences (append) because
            // ScriptOptions.Default contains 25 "Unresolved" named references that can't
            // be resolved without TRUSTED_PLATFORM_ASSEMBLIES — they just add noise.
            var allReferences = new List<MetadataReference>(bclRefs.Length + _compilations.Count);
            allReferences.AddRange(bclRefs);
            foreach (var compilation in _compilations.Values)
                allReferences.Add(compilation.ToMetadataReference());

            // Runtime assemblies the analyzed source doesn't carry
            // (UnityEngine.dll, UnityEditor.dll, ASP.NET runtime pack, etc.).
            // The resolver returns absolute paths; we filter to existing files
            // and skip duplicates by file name to avoid Roslyn's
            // "duplicate assembly identity" warning when multiple build
            // outputs ship the same UnityEngine.CoreModule.dll.
            if (_runtimeAssemblies != null)
            {
                var seenAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in _runtimeAssemblies.GetAssemblyProbePaths())
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!File.Exists(p)) continue;
                    var key = Path.GetFileName(p);
                    if (!seenAssemblyNames.Add(key)) continue;
                    try
                    {
                        allReferences.Add(MetadataReference.CreateFromFile(p));
                    }
                    catch
                    {
                        // Bad image / locked file — skip silently.
                    }
                }
            }

            // Plan v4 Seam #3: include the assemblies that define the script-
            // globals types (RoslynSemanticView and its property types) so the
            // script compiler can resolve `Graph`, `Compilations`, and
            // `ModuleDependencies` at top level. Without these, scripts that
            // reference globals get CS0246 even though the globals object is
            // actually present at runtime.
            allReferences.Add(MetadataReference.CreateFromFile(typeof(RoslynSemanticView).Assembly.Location));
            allReferences.Add(MetadataReference.CreateFromFile(typeof(Lifeblood.Domain.Graph.SemanticGraph).Assembly.Location));
            allReferences.Add(MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location));
            allReferences.Add(MetadataReference.CreateFromFile(typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location));

            var allImports = DefaultImports
                .Concat(new[]
                {
                    "Lifeblood.Adapters.CSharp",
                    "Lifeblood.Domain.Graph",
                    "Microsoft.CodeAnalysis",
                    "Microsoft.CodeAnalysis.CSharp",
                })
                .Concat(imports ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            var options = ScriptOptions.Default
                .WithReferences(allReferences)
                .WithImports(allImports);

            // Redirect Console output.
            // Thread-unsafe: global Console.Out/Error. Safe only because MCP server
            // is single-threaded. ProcessIsolatedCodeExecutor avoids this entirely.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var origOut = Console.Out;
            var origErr = Console.Error;

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);

                // Run on a separate thread so we can enforce hard timeout.
                // CancellationToken alone can't interrupt synchronous blocking
                // (Thread.Sleep, while(true){}, etc.) inside the script.
                using var cts = new CancellationTokenSource(timeoutMs);
                // Plan v4 Seam #3: pass the RoslynSemanticView as script globals.
                // CSharpScript.RunAsync<TGlobals> exposes globals' instance members
                // at script top-level scope, so the script reads `Graph`,
                // `Compilations`, `ModuleDependencies` as bare identifiers.
                var scriptTask = Task.Run(() =>
                    CSharpScript.RunAsync(code, options, globals: _view,
                            globalsType: typeof(RoslynSemanticView),
                            cancellationToken: cts.Token)
                        .GetAwaiter().GetResult(),
                    cts.Token);

                if (!scriptTask.Wait(timeoutMs))
                {
                    cts.Cancel();
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Error = $"Execution timed out after {timeoutMs}ms",
                        ElapsedMs = timeoutMs,
                    };
                }

                var scriptState = scriptTask.Result;
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var returnValue = scriptState.ReturnValue;

                return new CodeExecutionResult
                {
                    Success = true,
                    Output = stdout.ToString(),
                    Error = stderr.ToString(),
                    ReturnValue = returnValue?.ToString(),
                    ElapsedMs = Math.Round(elapsed, 1),
                    RuntimeAssemblyWarnings = runtimeAssemblyDiagnostics,
                    TargetRuntimeWarnings = targetWarnings,
                };
            }
            finally
            {
                Console.SetOut(origOut);
                Console.SetError(origErr);
            }
        }
        catch (CompilationErrorException ex)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            return new CodeExecutionResult
            {
                Success = false,
                Error = string.Join("\n", ex.Diagnostics.Select(d => d.ToString())),
                ElapsedMs = Math.Round(elapsed, 1),
                RuntimeAssemblyWarnings = runtimeAssemblyDiagnostics,
                TargetRuntimeWarnings = targetWarnings,
            };
        }
        catch (OperationCanceledException)
        {
            return new CodeExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutMs}ms",
                ElapsedMs = timeoutMs,
                RuntimeAssemblyWarnings = runtimeAssemblyDiagnostics,
                TargetRuntimeWarnings = targetWarnings,
            };
        }
        catch (Exception ex)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            return new CodeExecutionResult
            {
                Success = false,
                Error = ex.InnerException?.Message ?? ex.Message,
                ElapsedMs = Math.Round(elapsed, 1),
                RuntimeAssemblyWarnings = runtimeAssemblyDiagnostics,
                TargetRuntimeWarnings = targetWarnings,
            };
        }
    }

    /// <summary>
    /// Pick the BCL reference set for the requested target profile.
    /// Returns the references plus any non-fatal diagnostics
    /// (unknown profile, ref-pack not installed locally, etc.). Falls
    /// back to the host BCL on any miss so the script still has SOME
    /// reference set. See `INV-EXECUTE-001`.
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

        // Pack directory candidates exist on this OS but none were found
        // installed. Fall back to host BCL with a clear diagnostic.
        return (HostBclReferences.Value, new[]
        {
            $"Target profile '{profile}' selected but no matching reference pack was found on this machine. " +
            $"Searched: {string.Join(", ", packPaths)}. Falling back to host runtime BCL — " +
            "scripts may compile against APIs that are not present at the requested target.",
        });
    }

    /// <summary>
    /// Candidate directories where the requested target profile's
    /// reference pack might be installed. Order matters: the first
    /// directory that actually exists and contains DLLs wins.
    /// </summary>
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
                // SDK installs ship the netstandard ref-pack
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
                        // Pick the highest installed 6.x patch.
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
        // Standard locations the dotnet SDK uses for shared / per-user packs.
        yield return Path.Combine(programFiles, "dotnet", "packs");
        if (!string.IsNullOrEmpty(userHome))
            yield return Path.Combine(userHome, ".dotnet", "packs");
    }

    /// <summary>
    /// Load the host .NET runtime's BCL assemblies from the runtime directory.
    /// This gives script code access to System.Object, System.Linq, System.Console, etc.
    /// without depending on ScriptOptions.Default's "Unresolved" named references.
    /// </summary>
    private static MetadataReference[] LoadHostBclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null)
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        // Core BCL assemblies needed for script execution.
        // Intentionally explicit rather than globbing *.dll — avoids pulling in
        // ASP.NET, WPF, etc. assemblies that slow compilation and enlarge the type space.
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

        // Always include the core lib (contains System.Object, System.Int32, etc.)
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        return refs.ToArray();
    }

    /// <summary>
    /// Collapse whitespace around member-access dots so "Process . Start" becomes "Process.Start".
    /// This prevents trivial bypass of string-based blocklist patterns.
    /// Only normalizes for pattern matching — the original code is still executed.
    /// </summary>
    private static string NormalizeMemberAccess(string code)
    {
        // Collapse: "foo . bar" → "foo.bar", "foo .bar" → "foo.bar", "foo. bar" → "foo.bar"
        // Also collapse "new  FileInfo" → "new FileInfo" (multi-space between new and type)
        var sb = new System.Text.StringBuilder(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '.' && sb.Length > 0)
            {
                // Trim trailing whitespace before dot
                while (sb.Length > 0 && char.IsWhiteSpace(sb[sb.Length - 1]))
                    sb.Length--;
                sb.Append('.');
                // Skip leading whitespace after dot
                while (i + 1 < code.Length && char.IsWhiteSpace(code[i + 1]))
                    i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
