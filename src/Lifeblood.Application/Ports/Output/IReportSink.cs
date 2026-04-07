using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Output;

public interface IReportSink
{
    void Write(AnalysisResult result, Stream destination);
    string Format { get; }
}
