using System.Diagnostics;
using System.Text.Json;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Process-isolated code execution. Spawns Lifeblood.ScriptHost as a child process.
/// Timeout is a real Process.Kill — truly unkillable from the script's perspective.
/// No shared memory, no parent state access. Isolation is physical.
///
/// ICodeExecutor port interface is unchanged — consumers don't know the impl.
/// </summary>
public sealed class ProcessIsolatedCodeExecutor : ICodeExecutor
{
    private readonly string _scriptHostPath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <param name="scriptHostPath">
    /// Path to the Lifeblood.ScriptHost project directory or compiled DLL.
    /// If a directory, uses "dotnet run --project {path}".
    /// If a DLL, uses "dotnet {path}".
    /// </param>
    public ProcessIsolatedCodeExecutor(string scriptHostPath)
    {
        _scriptHostPath = scriptHostPath;
    }

    public CodeExecutionResult Execute(string code, string[]? imports = null, int timeoutMs = 5000)
    {
        var request = JsonSerializer.Serialize(new { code, imports }, JsonOpts);

        var (command, args) = _scriptHostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", _scriptHostPath)
            : ("dotnet", $"run --project {_scriptHostPath} --no-build");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return new CodeExecutionResult { Success = false, Error = "Failed to start script host process" };

            // Write request to stdin, then close it
            process.StandardInput.Write(request);
            process.StandardInput.Close();

            // Read stdout/stderr BEFORE WaitForExit to avoid pipe deadlock.
            // If child writes more than the pipe buffer, WaitForExit blocks forever.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Wait for completion with hard timeout — a real Process.Kill, not cooperative cancel
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return new CodeExecutionResult { Success = false, Error = $"Execution timed out after {timeoutMs}ms (process killed)" };
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 && string.IsNullOrEmpty(stdout))
                return new CodeExecutionResult { Success = false, Error = $"Script host exited with code {process.ExitCode}: {stderr}" };

            // Parse the JSON result from the child process
            try
            {
                var result = JsonSerializer.Deserialize<ProcessResult>(stdout, JsonOpts);
                if (result == null)
                    return new CodeExecutionResult { Success = false, Error = "Empty response from script host" };

                return new CodeExecutionResult
                {
                    Success = result.Success,
                    ReturnValue = result.ReturnValue,
                    Output = result.Output ?? "",
                    Error = result.Error ?? result.ErrorOutput ?? "",
                };
            }
            catch (JsonException)
            {
                return new CodeExecutionResult { Success = false, Error = $"Invalid JSON from script host: {stdout}" };
            }
        }
        catch (Exception ex)
        {
            return new CodeExecutionResult { Success = false, Error = $"Process execution failed: {ex.Message}" };
        }
    }

    private sealed class ProcessResult
    {
        public bool Success { get; set; }
        public string? ReturnValue { get; set; }
        public string? Output { get; set; }
        public string? ErrorOutput { get; set; }
        public string? Error { get; set; }
    }
}
