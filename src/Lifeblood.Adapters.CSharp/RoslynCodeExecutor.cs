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

    public RoslynCodeExecutor(IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _compilations = compilations;
    }

    public CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;

        // Layer 1: String-based blocklist (fast, catches obvious patterns)
        foreach (var pattern in BlockedPatterns)
        {
            if (code.Contains(pattern, StringComparison.OrdinalIgnoreCase))
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
            // Build script options with all project references + BCL
            var allReferences = new List<MetadataReference>();
            foreach (var compilation in _compilations.Values)
            {
                allReferences.Add(compilation.ToMetadataReference());
                allReferences.AddRange(compilation.References);
            }

            // Deduplicate by display name
            var uniqueRefs = allReferences
                .GroupBy(r => r.Display ?? "")
                .Select(g => g.First())
                .ToArray();

            var allImports = DefaultImports
                .Concat(imports ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            var options = ScriptOptions.Default
                .WithReferences(uniqueRefs)
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
}
