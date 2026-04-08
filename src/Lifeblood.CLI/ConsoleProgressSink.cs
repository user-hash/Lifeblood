using Lifeblood.Application.Ports.Output;

namespace Lifeblood.CLI;

/// <summary>
/// Writes progress to stderr so stdout stays clean for piped output.
/// </summary>
internal sealed class ConsoleProgressSink : IProgressSink
{
    public void Report(string phase, int current, int total) =>
        Console.Error.WriteLine($"[{current}/{total}] {phase}");

    public void Log(string message) =>
        Console.Error.WriteLine(message);
}
