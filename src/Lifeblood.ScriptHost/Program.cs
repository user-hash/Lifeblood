using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Lifeblood.ScriptHost;

/// <summary>
/// Minimal process-isolated script execution harness.
/// Reads a JSON request from stdin, compiles + runs the script,
/// writes a JSON result to stdout. Parent process controls lifetime via Process.Kill.
///
/// This runs as a separate process — no shared memory, no parent state access.
/// Isolation is physical, not logical.
///
/// SECURITY NOTE: This harness has no blocklist or AST scanner. It relies on
/// process-level isolation (the parent controls lifetime via Process.Kill).
/// The script CAN access the filesystem and network with the user's permissions.
/// For untrusted code, deploy with OS-level restrictions (containers, AppContainers).
/// </summary>
class Program
{
    static async Task<int> Main()
    {
        try
        {
            var inputJson = await Console.In.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<ScriptRequest>(inputJson,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            if (request == null || string.IsNullOrEmpty(request.Code))
            {
                WriteResult(new ScriptResult { Error = "Empty or invalid request" });
                return 1;
            }

            var imports = request.Imports ?? Array.Empty<string>();
            var options = ScriptOptions.Default
                .AddReferences(typeof(object).Assembly, typeof(Console).Assembly, typeof(Enumerable).Assembly)
                .AddImports("System", "System.Linq", "System.Collections.Generic");

            foreach (var import in imports)
                options = options.AddImports(import);

            // Redirect Console.Out/Error for capture
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            Console.SetOut(stdout);
            Console.SetError(stderr);

            try
            {
                var scriptState = await CSharpScript.RunAsync(request.Code, options);
                var returnValue = scriptState.ReturnValue;

                Console.SetOut(originalOut);
                Console.SetError(originalErr);

                WriteResult(new ScriptResult
                {
                    Success = true,
                    ReturnValue = returnValue?.ToString(),
                    Output = stdout.ToString(),
                    ErrorOutput = stderr.ToString(),
                });
                return 0;
            }
            catch (CompilationErrorException ex)
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
                WriteResult(new ScriptResult { Error = string.Join("\n", ex.Diagnostics) });
                return 1;
            }
            catch (Exception ex)
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
                WriteResult(new ScriptResult
                {
                    Error = $"Runtime error: {ex.GetType().Name}: {ex.Message}",
                    Output = stdout.ToString(),
                    ErrorOutput = stderr.ToString(),
                });
                return 1;
            }
        }
        catch (Exception ex)
        {
            // Last resort — write to original stdout
            WriteResult(new ScriptResult { Error = $"Host error: {ex.Message}" });
            return 2;
        }
    }

    static void WriteResult(ScriptResult result)
    {
        var json = JsonSerializer.Serialize(result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Console.Out.Write(json);
        Console.Out.Flush();
    }
}

sealed class ScriptRequest
{
    public string Code { get; set; } = "";
    public string[]? Imports { get; set; }
}

sealed class ScriptResult
{
    public bool Success { get; set; }
    public string? ReturnValue { get; set; }
    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }
    public string? Error { get; set; }
}
