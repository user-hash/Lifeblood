using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.GraphIO;

public interface IGraphExporter
{
    void Export(GraphDocument document, Stream destination);
    string Format { get; }
}
