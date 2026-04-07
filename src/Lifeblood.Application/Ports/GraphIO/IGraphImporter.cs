using Lifeblood.Domain.Graph;

namespace Lifeblood.Application.Ports.GraphIO;

/// <summary>
/// INV-ADAPT-003: External adapters communicate via JSON graph schema.
/// </summary>
public interface IGraphImporter
{
    SemanticGraph Import(Stream source);
    string Format { get; }
}
