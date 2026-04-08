namespace WriteSideApp.Core;

/// <summary>
/// Port interface. Demonstrates: overloaded methods, event declaration.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
    string Greet(string firstName, string lastName);

    /// <summary>Fired after every greeting. Tests event symbol extraction.</summary>
    event EventHandler? GreetingMade;
}
