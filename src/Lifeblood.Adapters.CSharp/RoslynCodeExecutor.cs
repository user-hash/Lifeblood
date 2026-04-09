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
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;

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

    public RoslynCodeExecutor(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
    }

    public CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;

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
            var allReferences = new List<MetadataReference>(HostBclReferences.Value.Length + _compilations.Count);
            allReferences.AddRange(HostBclReferences.Value);
            foreach (var compilation in _compilations.Values)
                allReferences.Add(compilation.ToMetadataReference());

            var allImports = DefaultImports
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
                var scriptTask = Task.Run(() =>
                    CSharpScript.RunAsync(code, options, cancellationToken: cts.Token)
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
            };
        }
        catch (OperationCanceledException)
        {
            return new CodeExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutMs}ms",
                ElapsedMs = timeoutMs,
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
            };
        }
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
