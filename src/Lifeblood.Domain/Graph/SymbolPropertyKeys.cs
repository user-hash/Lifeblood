namespace Lifeblood.Domain.Graph;

/// <summary>
/// Canonical property-key constants for <see cref="Symbol.Properties"/>.
/// Every writer (extractor) and every reader (analyzer / classifier /
/// reachability provider) MUST reference these constants instead of
/// literal strings.
///
/// The drift class this guards: a reader looking up a property key the
/// writer never emitted (e.g. <c>TierClassifier</c> reading
/// <c>Properties["isTooling"]</c> when the extractor only wrote
/// <c>"attributes"</c>) is a permanent dead read with no compile-time
/// signal. Centralizing the key set here makes the writer/reader pair
/// impossible to drift in isolation — pinned by
/// <c>SymbolPropertyKeysParityTests</c>. INV-PROPERTY-KEY-PARITY-001.
/// </summary>
public static class SymbolPropertyKeys
{
    /// <summary>
    /// Semicolon-separated set of attribute simple-names declared on a
    /// symbol (e.g. <c>"Test;TestCase"</c>). Written by
    /// <c>RoslynSymbolExtractor</c> from <c>ISymbol.GetAttributes()</c>;
    /// read by <c>TierClassifier</c> (Tooling tier),
    /// <c>TestImpactAnalyzer</c> (test-case fold),
    /// <c>UnityReachabilityAdapter</c> (runtime-entrypoint detection).
    /// </summary>
    public const string Attributes = "attributes";

    /// <summary>
    /// Fully-qualified direct base type name for source types when the
    /// base is more specific than <c>object</c> / <c>ValueType</c> /
    /// <c>Enum</c> / <c>Delegate</c>. Written by
    /// <c>RoslynSymbolExtractor</c>; read by
    /// <c>UnityReachabilityAdapter</c> to walk Unity runtime-dispatch
    /// inheritance chains even when the base lives outside the graph.
    /// </summary>
    public const string BaseType = "baseType";

    /// <summary>
    /// Semicolon-separated fully-qualified base type chain from direct
    /// base to root, excluding universal framework roots such as
    /// <c>object</c>. Written by <c>RoslynSymbolExtractor</c>; read by
    /// <c>UnityReachabilityAdapter</c> so framework-dispatch checks can
    /// follow metadata-defined inheritance chains without maintaining
    /// every intermediate framework subclass by hand.
    /// </summary>
    public const string BaseTypeChain = "baseTypeChain";

    /// <summary>
    /// Method-body shape classification written by
    /// <c>RoslynSymbolExtractor</c> and read by
    /// <c>LifebloodAuthorityReporter</c> for forwarder-ratio evidence.
    /// </summary>
    public const string Classification = "classification";

    /// <summary>
    /// Source field type display string written by <c>RoslynSymbolExtractor</c>.
    /// Read by graph-only analyzers that need field-shape facts without
    /// reopening a Roslyn compilation.
    /// </summary>
    public const string FieldType = "fieldType";

    /// <summary>
    /// Compile-time constant value written for fields whose source symbol has
    /// a constant value. Enum members have always carried this key; ordinary
    /// const fields also carry it so graph-only analyzers can recognize
    /// constant-anchor shapes without re-opening a Roslyn compilation.
    /// </summary>
    public const string ConstantValue = "constantValue";

    /// <summary>
    /// Module reference-closure mode written by
    /// <c>RoslynModuleDiscovery</c> on module symbols and read by
    /// <c>AsmdefBoundaryAnalyzer</c>. Values mirror
    /// <c>ReferenceClosureMode</c> names.
    /// </summary>
    public const string ReferenceClosure = "referenceClosure";
}
