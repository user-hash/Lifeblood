using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Ports;

/// <summary>
/// Reads a pre-built semantic graph from an external source.
///
/// INV-PORT-003: External adapters communicate via JSON graph schema only.
/// A Python adapter runs as a separate process, outputs graph.json,
/// and this port reads it into the core.
/// </summary>
public interface IGraphImporter
{
    /// <summary>
    /// Import a semantic graph from a stream (file, stdin, network).
    /// </summary>
    SemanticGraph Import(Stream source);

    /// <summary>Supported format (e.g., "json").</summary>
    string Format { get; }
}
