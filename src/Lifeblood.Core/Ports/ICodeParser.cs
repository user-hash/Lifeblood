using Lifeblood.Core.Graph;

namespace Lifeblood.Core.Ports;

/// <summary>
/// THE key port interface. Every language adapter implements this.
/// Parses a single source file into symbols and edges.
///
/// INV-ADAPTER-001: This is the only required interface for a language adapter.
/// INV-CORE-001: Core never references any concrete parser.
/// </summary>
public interface ICodeParser
{
    /// <summary>
    /// Parse a source file into symbols and edges.
    /// </summary>
    /// <param name="filePath">Path to the source file (relative to project root).</param>
    /// <param name="sourceText">Full text content of the file.</param>
    /// <returns>Symbols found in this file and dependency edges originating from it.</returns>
    ParseResult Parse(string filePath, string sourceText);

    /// <summary>File extensions this parser handles (e.g., [".cs"], [".py", ".pyi"]).</summary>
    string[] SupportedExtensions { get; }
}

/// <summary>
/// Result of parsing a single source file.
/// </summary>
public sealed class ParseResult
{
    /// <summary>Symbols discovered in this file (types, methods, fields).</summary>
    public Symbol[] Symbols { get; init; } = Array.Empty<Symbol>();

    /// <summary>Dependency edges originating from this file.</summary>
    public Edge[] Edges { get; init; } = Array.Empty<Edge>();

    /// <summary>File-level metadata.</summary>
    public FileMetadata Metadata { get; init; } = new();
}

/// <summary>
/// Metadata about a parsed source file.
/// </summary>
public sealed class FileMetadata
{
    /// <summary>Primary namespace/package/module of this file.</summary>
    public string Namespace { get; init; } = "";

    /// <summary>Import/using statements.</summary>
    public string[] Imports { get; init; } = Array.Empty<string>();

    /// <summary>Lines of code (excluding blanks and comments).</summary>
    public int LinesOfCode { get; init; }

    /// <summary>Total lines including blanks and comments.</summary>
    public int TotalLines { get; init; }
}
