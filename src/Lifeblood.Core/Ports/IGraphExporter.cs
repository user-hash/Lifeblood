using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Ports;

/// <summary>
/// Serializes a semantic graph for external consumption.
/// JSON for tooling, DOT for Graphviz, custom formats for dashboards.
/// </summary>
public interface IGraphExporter
{
    /// <summary>
    /// Export the graph to a stream.
    /// </summary>
    void Export(SemanticGraph graph, Stream destination);

    /// <summary>Output format (e.g., "json", "dot", "csv").</summary>
    string Format { get; }
}
