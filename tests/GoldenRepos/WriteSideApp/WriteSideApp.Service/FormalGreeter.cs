using WriteSideApp.Core;

namespace WriteSideApp.Service;

/// <summary>
/// Derived class. Demonstrates: inheritance, method override, base class reference.
/// </summary>
public class FormalGreeter : Greeter
{
    public override string Greet(string name)
    {
        OnGreetingMade();
        return $"Good day, {name}.";
    }
}
