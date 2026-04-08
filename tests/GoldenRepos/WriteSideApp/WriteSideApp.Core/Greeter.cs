namespace WriteSideApp.Core;

public class Greeter : IGreeter
{
    public string Name { get; set; } = "World";

    public string Greet(string name) => $"Hello, {name}!";

    public string Greet(string firstName, string lastName) => $"Hello, {firstName} {lastName}!";
}
