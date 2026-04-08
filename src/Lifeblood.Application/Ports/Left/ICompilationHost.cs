using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Provides compilation-level capabilities: diagnostics, compile-checking, reference finding.
/// Language-agnostic contract — Roslyn implements with CSharpCompilation.
/// </summary>
public interface ICompilationHost
{
    bool IsAvailable { get; }
    DiagnosticInfo[] GetDiagnostics(string? moduleName = null);
    CompileCheckResult CompileCheck(string code, string? moduleName = null);
    ReferenceLocation[] FindReferences(string symbolId);

    /// <summary>Find where a symbol is declared (definition site).</summary>
    DefinitionLocation? FindDefinition(string symbolId);

    /// <summary>Find all types that implement an interface or override a virtual member.</summary>
    string[] FindImplementations(string symbolId);

    /// <summary>Resolve the symbol at a source position (file + line + column).</summary>
    SymbolAtPosition? GetSymbolAtPosition(string filePath, int line, int column);

    /// <summary>Get XML documentation for a symbol.</summary>
    string GetDocumentation(string symbolId);
}
