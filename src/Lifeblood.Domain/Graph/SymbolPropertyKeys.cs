namespace Lifeblood.Domain.Graph;

/// <summary>
/// Canonical property-key constants for <see cref="Symbol.Properties"/>.
/// Every writer (extractor) and every reader (analyzer / classifier /
/// reachability provider) MUST reference these constants instead of
/// literal strings.
///
/// BUG-4 (2026-05-14) was exactly this drift shape: <c>TierClassifier</c>
/// read <c>Properties["isTooling"]</c> which the extractor never wrote
/// (permanent dead read, silent classification miss). Centralizing the
/// key set in one place makes the writer/reader pair impossible to
/// drift in isolation — pinned by <c>SymbolPropertyKeysParityTests</c>.
///
/// INV-PROPERTY-KEY-PARITY-001 / LB-FOLLOWUP-20260514-004.
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
