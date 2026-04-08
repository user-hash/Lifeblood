namespace Lifeblood.Application.Ports.Output;

public interface IProgressSink
{
    void Report(string phase, int current, int total);
}
