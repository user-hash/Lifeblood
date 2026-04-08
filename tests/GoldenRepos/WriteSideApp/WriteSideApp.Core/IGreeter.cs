namespace WriteSideApp.Core;

public interface IGreeter
{
    string Greet(string name);
    string Greet(string firstName, string lastName);
}
