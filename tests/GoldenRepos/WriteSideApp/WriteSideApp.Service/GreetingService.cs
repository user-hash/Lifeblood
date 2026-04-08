using WriteSideApp.Core;

namespace WriteSideApp.Service;

public class GreetingService
{
    private readonly IGreeter _greeter;

    public GreetingService(IGreeter greeter)
    {
        _greeter = greeter;
    }

    public string GetGreeting()
    {
        return _greeter.Greet("User");
    }

    public string GetFullGreeting(string first, string last)
    {
        return _greeter.Greet(first, last);
    }
}
