namespace Lifeblood.Core.Graph;

/// <summary>
/// A node in the semantic graph. Represents any identifiable code element.
/// Language-agnostic: no C#-isms, no Python-isms. Only universal concepts.
/// Language-specific metadata goes in Properties.
/// </summary>
public sealed class Symbol
{
    /// <summary>
    /// Globally unique within a graph.
    /// Format: "{filePath}:{kind}:{qualifiedName}" or adapter-defined.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>Short display name (e.g., "AuthService", "Validate", "retryCount").</summary>
    public string Name { get; init; } = "";

    /// <summary>Fully qualified name including namespace (e.g., "MyApp.Auth.AuthService").</summary>
    public string QualifiedName { get; init; } = "";

    /// <summary>What kind of code element this is.</summary>
    public SymbolKind Kind { get; init; }

    /// <summary>Source file path (relative to project root).</summary>
    public string FilePath { get; init; } = "";

    /// <summary>Line number in source (1-based). 0 if unknown.</summary>
    public int Line { get; init; }

    /// <summary>
    /// Parent symbol ID in the containment hierarchy.
    /// File's parent = Module. Type's parent = File. Method's parent = Type.
    /// Empty string for root-level symbols.
    /// </summary>
    public string ParentId { get; init; } = "";

    /// <summary>
    /// Visibility: Public, Internal, Protected, Private.
    /// Mapped from language-specific access modifiers.
    /// </summary>
    public Visibility Visibility { get; init; } = Visibility.Internal;

    /// <summary>True if this is abstract/interface/trait (no implementation).</summary>
    public bool IsAbstract { get; init; }

    /// <summary>True if this is static/class-level.</summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Language-specific metadata. Core never reads this.
    /// Adapters can store anything here (e.g., "async": "true", "decorator": "@cached").
    /// INV-GRAPH-002: Language-specific data goes here, not in new fields.
    /// </summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}

/// <summary>
/// Universal code element kinds.
/// INV-GRAPH-001: No language-specific kinds. Only universals.
/// </summary>
public enum SymbolKind
{
    Module,     // Assembly, package, crate, npm package
    File,       // Source file
    Namespace,  // Namespace, package path, module path
    Type,       // Class, struct, interface, enum, trait, protocol
    Method,     // Method, function, property accessor, constructor
    Field,      // Field, constant, static variable
    Parameter,  // Method parameter
}

public enum Visibility
{
    Public,
    Internal,   // C# internal, Go package-level, Python convention
    Protected,
    Private,
}
