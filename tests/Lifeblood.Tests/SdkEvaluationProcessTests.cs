using System.Diagnostics;
using Lifeblood.Adapters.CSharp.Internal;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SdkEvaluationProcessTests
{
    [Fact]
    public void Capture_DrainsBothPipesBeyondTheirBuffers()
    {
        var start = Shell(
            "[Console]::Out.Write(('o' * 131072)); [Console]::Error.Write(('e' * 131072))",
            "printf '%131072s' '' | tr ' ' o; printf '%131072s' '' | tr ' ' e >&2");
        Assert.Equal(new string('o', 131072), SdkProjectFactsReader.RunEvaluation(start, 10_000));
    }

    [Fact]
    public void Capture_ClosesInputAndPreservesNonzeroExitDiagnostics()
    {
        var start = Shell(
            "[Console]::Out.Write([Console]::In.ReadToEnd()); [Console]::Error.Write('sdk failure'); exit 7",
            "cat; printf 'sdk failure' >&2; exit 7");
        var failure = Assert.Throws<InvalidOperationException>(() => SdkProjectFactsReader.RunEvaluation(start, 10_000));
        Assert.Contains("SDK project evaluation failed: sdk failure", failure.Message);
    }

    [Fact]
    public void Timeout_KillsAndReapsTheOwnedProcessTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-sdk-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var receipt = Path.Combine(root, "processes.txt");
        try
        {
            var windows = "$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d /c ping -n 60 127.0.0.1 >nul' -WindowStyle Hidden -PassThru; "
                + $"[IO.File]::WriteAllText('{receipt.Replace("'", "''")}', \"$PID $($child.Id)\"); Start-Sleep -Seconds 60";
            var unix = $"sleep 60 & child=$!; printf '%s %s' $$ $child > '{receipt.Replace("'", "'\\''")}'; wait";
            // Include shell and child startup in the same 30-second budget as SDK evaluation.
            var failure = Assert.Throws<InvalidOperationException>(() => SdkProjectFactsReader.RunEvaluation(Shell(windows, unix), 30_000));
            Assert.IsType<TimeoutException>(failure.InnerException);
            Assert.True(File.Exists(receipt), "The process tree must start before exercising its deadline.");
            foreach (var id in File.ReadAllText(receipt).Split(' ').Select(int.Parse))
                Assert.False(IsRunning(id), $"Process {id} survived SDK evaluation timeout.");
        }
        finally
        {
            if (File.Exists(receipt))
                foreach (var id in File.ReadAllText(receipt).Split(' ').Select(int.Parse))
                    try { using var process = Process.GetProcessById(id); if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (ArgumentException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command", windows }
            : new[] { "-c", unix }) start.ArgumentList.Add(argument);
        return start;
    }
}
