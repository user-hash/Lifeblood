namespace WriteSideApp.Core;

/// <summary>
/// Base implementation. Demonstrates: interface impl, virtual methods, event raising, property.
/// </summary>
public class Greeter : IGreeter
{
    public string Name { get; set; } = "World";

    public virtual string Greet(string name)
    {
        OnGreetingMade();
        return $"Hello, {name}!";
    }

    public string Greet(string firstName, string lastName)
    {
        OnGreetingMade();
        return $"Hello, {firstName} {lastName}!";
    }

    public event EventHandler? GreetingMade;

    protected virtual void OnGreetingMade()
        => GreetingMade?.Invoke(this, EventArgs.Empty);
}
