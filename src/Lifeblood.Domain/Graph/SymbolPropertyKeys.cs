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
}
