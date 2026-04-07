using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.GraphIO;

public interface IGraphExporter
{
    void Export(SemanticGraph graph, Stream destination);
    string Format { get; }
}
