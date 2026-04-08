using WriteSideApp.Core;

namespace WriteSideApp.Service;

/// <summary>
/// Orchestrator. Demonstrates: cross-project references, event subscription,
/// overload calls, constructor injection, indexer usage.
/// </summary>
public class GreetingService
{
    private readonly IGreeter _greeter;
    private readonly GreetingLog _log = new();

    public GreetingService(IGreeter greeter)
    {
        _greeter = greeter;
        _greeter.GreetingMade += OnGreetingMade;
    }

    public string GetGreeting()
        => _greeter.Greet("User");

    public string GetFullGreeting(string first, string last)
        => _greeter.Greet(first, last);

    public string GetLogEntry(int index)
        => _log[index];

    private void OnGreetingMade(object? sender, EventArgs e)
        => _log.Add("Greeting was made");
}
