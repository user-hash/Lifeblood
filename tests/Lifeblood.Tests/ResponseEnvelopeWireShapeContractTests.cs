using System.Linq;
using System.Reflection;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Wire-shape contract pin for <see cref="ResponseEnvelope"/>
/// (INV-WIRE-CONTRACT-001 / LB-INBOX-003 Wave W4-A). Every read-side
/// MCP tool response embeds an envelope; consumers across multiple
/// language SDKs build against its field set. A rename, a type
/// change, or a quiet removal would break every external integrator
/// silently. This snapshot locks the v1 wire shape by reflection so
/// the ratchet fires on any structural change.
///
/// Adding a field is non-breaking and requires bumping the expected
/// set below. Renaming or removing a field is breaking — bump the
/// schema version (`v1` → `v2`), keep the v1 snapshot frozen on a
/// historical branch / regression file, and write the deprecation
/// rationale into <c>docs/SCHEMA_DEPRECATION_POLICY.md</c>.
/// </summary>
public class ResponseEnvelopeWireShapeContractTests
{
    /// <summary>
    /// Canonical v1 field set. Tuple of (PropertyName, PropertyType).
    /// The type is stored as a stable name so a future Roslyn version
    /// renaming the underlying type alias does not silently shift the
    /// contract — the consumer cares about wire shape, not CLR type
    /// identity.
    /// </summary>
    private static readonly (string Name, string TypeName)[] V1FieldSet =
    {
        ("TruthTier",                "TruthTier"),
        ("Confidence",               "ConfidenceBand"),
        ("EvidenceSource",           "String"),
        ("StalenessSeconds",         "Int64"),
        ("FilesChangedSinceAnalyze", "Int32"),
        ("Limitations",              "String[]"),
        // S5 / INV-DIAGNOSE-FRESHNESS-001: monotonic generation counter
        // for cross-tool join coherence. Non-breaking addition.
        ("AnalysisGeneration",       "Int64"),
    };

    [Fact]
    public void ResponseEnvelope_FieldSet_MatchesV1Contract()
    {
        var actual = typeof(ResponseEnvelope)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, System.StringComparer.Ordinal)
            .Select(p => (Name: p.Name, TypeName: FormatTypeName(p.PropertyType)))
            .ToArray();

        var expected = V1FieldSet
            .OrderBy(t => t.Name, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResponseEnvelope_FieldsAreInitOnly_NotMutableSetters()
    {
        // Mutation after construction is a wire-contract hazard: the
        // decorator builds the envelope once at the composition root
        // and downstream code reads it. Allowing a public setter would
        // open a race where a tool handler accidentally rewrites a
        // field after the decorator returned. Lock with `init`.
        foreach (var prop in typeof(ResponseEnvelope).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setMethod = prop.SetMethod;
            Assert.True(setMethod != null,
                $"{prop.Name} has no setter — the contract requires `init` setters.");
            // `init`-only setters carry a modreq for IsExternalInit. A
            // plain `set` setter does not. Detect by looking for the
            // modifier on the SetMethod's return parameter.
            var modreqs = setMethod!.ReturnParameter.GetRequiredCustomModifiers();
            Assert.Contains(modreqs, m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }
    }

    private static string FormatTypeName(System.Type t)
    {
        if (t.IsArray) return FormatTypeName(t.GetElementType()!) + "[]";
        // Strip nullable wrapper so `string?` and `string` compare identically.
        var underlying = System.Nullable.GetUnderlyingType(t);
        if (underlying != null) return FormatTypeName(underlying);
        return t.Name;
    }
}
