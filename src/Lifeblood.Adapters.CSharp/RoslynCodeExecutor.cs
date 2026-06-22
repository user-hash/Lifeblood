using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
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

    private static readonly IReadOnlyDictionary<string, Type> ScriptSurfaceHintTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Edge"] = typeof(Edge),
            ["SemanticGraph"] = typeof(SemanticGraph),
            ["Symbol"] = typeof(Symbol),
            ["RoslynSemanticView"] = typeof(RoslynSemanticView),
        };

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

        // Build the reference set once through the adapter-owned seam so
        // every result path reports the same non-fatal diagnostics.
        var referenceSet = new ScriptReferenceSetBuilder(_compilations, _runtimeAssemblies)
            .Build(request.TargetProfile);

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
                    RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                    TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
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
                RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
            };

        try
        {
            // We use WithReferences (replace) instead of AddReferences (append) because
            // ScriptOptions.Default contains 25 "Unresolved" named references that can't
            // be resolved without TRUSTED_PLATFORM_ASSEMBLIES — they just add noise.
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
                .WithReferences(referenceSet.References)
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
                    RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                    TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
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
                Error = FormatCompilationDiagnostics(ex.Diagnostics),
                ElapsedMs = Math.Round(elapsed, 1),
                RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
            };
        }
        catch (OperationCanceledException)
        {
            return new CodeExecutionResult
            {
                Success = false,
                Error = $"Execution timed out after {timeoutMs}ms",
                ElapsedMs = timeoutMs,
                RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
            };
        }
        catch (Exception ex)
        {
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            if (TryFindCompilationError(ex, out var compilationError))
                return new CodeExecutionResult
                {
                    Success = false,
                    Error = FormatCompilationDiagnostics(compilationError.Diagnostics),
                    ElapsedMs = Math.Round(elapsed, 1),
                    RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                    TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
                };

            if (TryClassifyWorkspaceLoadBoundary(ex, out var assemblyName))
                return new CodeExecutionResult
                {
                    Success = false,
                    Error =
                        $"Workspace runtime-load boundary: lifeblood_execute compiles against " +
                        $"workspace/engine assembly '{assemblyName}' (injected as a Roslyn metadata " +
                        $"reference) but cannot load it into the analysis host at runtime. " +
                        $"Instantiation, Unsafe.SizeOf<T>, and reflection over workspace types are " +
                        $"unsupported by design. Use the Graph / Compilations symbol globals for " +
                        $"workspace-type facts; runtime values that need the workspace assembly " +
                        $"loaded must come from the engine's own runtime.",
                    ElapsedMs = Math.Round(elapsed, 1),
                    RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                    TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings
                        .Append(
                            $"compile-against-not-run boundary hit for workspace assembly " +
                            $"'{assemblyName}' — INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001")
                        .ToArray(),
                };
            return new CodeExecutionResult
            {
                Success = false,
                Error = ex.InnerException?.Message ?? ex.Message,
                ElapsedMs = Math.Round(elapsed, 1),
                RuntimeAssemblyWarnings = referenceSet.RuntimeAssemblyWarnings,
                TargetRuntimeWarnings = referenceSet.TargetRuntimeWarnings,
            };
        }
    }

    private static bool TryFindCompilationError(
        Exception ex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompilationErrorException? compilationError)
    {
        compilationError = Flatten(ex).OfType<CompilationErrorException>().FirstOrDefault();
        return compilationError != null;
    }

    private static string FormatCompilationDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var diagnosticArray = diagnostics.ToArray();
        var text = string.Join("\n", diagnosticArray.Select(d => d.ToString()));
        var cs1061Hint = BuildCs1061Hint(diagnosticArray);
        return string.IsNullOrEmpty(cs1061Hint)
            ? text
            : text + "\n\n" + cs1061Hint;
    }

    private static string BuildCs1061Hint(IEnumerable<Diagnostic> diagnostics)
    {
        var receiverTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics)
        {
            var diagnosticText = diagnostic.ToString();
            if (!string.Equals(diagnostic.Id, "CS1061", StringComparison.Ordinal)
                && !diagnosticText.Contains("CS1061", StringComparison.Ordinal))
                continue;

            var message = diagnostic.GetMessage();
            var receiverTypeName = ExtractQuotedReceiverTypeName(message)
                ?? ExtractQuotedReceiverTypeName(diagnosticText);
            var scriptingSurfaceName = ShortTypeName(receiverTypeName);
            if (scriptingSurfaceName != null && ScriptSurfaceHintTypes.ContainsKey(scriptingSurfaceName))
            {
                receiverTypes.Add(scriptingSurfaceName);
                continue;
            }

            foreach (var knownTypeName in ScriptSurfaceHintTypes.Keys)
            {
                if (message.Contains($"'{knownTypeName}'", StringComparison.Ordinal)
                    || message.Contains($".{knownTypeName}'", StringComparison.Ordinal)
                    || diagnosticText.Contains($"'{knownTypeName}'", StringComparison.Ordinal)
                    || diagnosticText.Contains($".{knownTypeName}'", StringComparison.Ordinal))
                {
                    receiverTypes.Add(knownTypeName);
                }
            }
        }

        if (receiverTypes.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("lifeblood_execute hint: CS1061 means the script referenced a member that is not on the Lifeblood scripting surface.");
        foreach (var receiverType in receiverTypes)
        {
            var members = PublicSurfaceMembers(ScriptSurfaceHintTypes[receiverType]);
            sb.Append(receiverType)
                .Append(" public members: ")
                .AppendLine(string.Join(", ", members));
        }
        sb.Append("Use the Help global for script examples and the Graph/Compilations/ModuleDependencies globals for workspace facts.");
        return sb.ToString();
    }

    private static string? ExtractQuotedReceiverTypeName(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0) return null;
        var end = message.IndexOf('\'', start + 1);
        if (end <= start + 1) return null;
        return message[(start + 1)..end];
    }

    private static string? ShortTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        var tick = typeName.IndexOf('`');
        if (tick >= 0)
            typeName = typeName[..tick];
        var dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    private static string[] PublicSurfaceMembers(Type type)
        => type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object))
            .Where(m => m.MemberType is System.Reflection.MemberTypes.Property
                or System.Reflection.MemberTypes.Field
                or System.Reflection.MemberTypes.Method)
            .Where(m => m is not System.Reflection.MethodInfo method || !method.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Detects the compile-against-but-not-run boundary
    /// (INV-EXECUTE-WORKSPACE-LOAD-BOUNDARY-001). Scripts compile against
    /// workspace/engine assemblies because those are injected as Roslyn
    /// metadata references, but the assemblies are never loaded into the
    /// analysis host runtime. Any script that forces a runtime load — type
    /// instantiation, <c>Unsafe.SizeOf&lt;T&gt;</c>, reflection over a
    /// workspace type — throws <see cref="FileLoadException"/> /
    /// <see cref="FileNotFoundException"/> at execution (often wrapped in
    /// <see cref="AggregateException"/> / <c>TargetInvocationException</c>).
    /// Walks the full exception chain and confirms the failing assembly is a
    /// known workspace module, so the raw loader message becomes a structured
    /// boundary instead of an "is this a tool bug?" leak.
    /// </summary>
    private bool TryClassifyWorkspaceLoadBoundary(Exception ex, out string assemblyName)
    {
        assemblyName = "";
        foreach (var inner in Flatten(ex))
        {
            string? fileName = inner switch
            {
                FileLoadException fle => fle.FileName,
                FileNotFoundException fnf => fnf.FileName,
                _ => null,
            };
            if (string.IsNullOrEmpty(fileName))
                continue;

            var simpleName = fileName.Split(',')[0].Trim();
            if (IsWorkspaceAssembly(simpleName))
            {
                assemblyName = simpleName;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="simpleName"/> is a loaded workspace module —
    /// matched against both the compilation dictionary keys and each
    /// compilation's <c>AssemblyName</c> so an asmdef whose module key differs
    /// from its assembly identity still resolves.
    /// </summary>
    private bool IsWorkspaceAssembly(string simpleName)
    {
        if (_compilations.ContainsKey(simpleName))
            return true;
        foreach (var compilation in _compilations.Values)
            if (string.Equals(compilation.AssemblyName, simpleName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Depth-first flatten of an exception and every inner /
    /// <see cref="AggregateException"/> child, so the workspace-load classifier
    /// sees loader exceptions wrapped by the scripting host's task machinery.
    /// </summary>
    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        var stack = new Stack<Exception>();
        stack.Push(ex);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var child in aggregate.InnerExceptions)
                    stack.Push(child);
            }
            else if (current.InnerException is not null)
            {
                stack.Push(current.InnerException);
            }
        }
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
