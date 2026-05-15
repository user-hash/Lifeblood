using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression suite for INV-ANALYZE-SKIPPED-PROMINENCE-001. The
/// staleness signal must surface as a
/// <see cref="ResponseEnvelope.Limitations"/> entry whenever it exceeds
/// the configured <see cref="StalenessPolicy"/> thresholds — pre-fix
/// the seconds + files-changed numbers were on the wire but consumers
/// had to compare them against unwritten thresholds to know whether to
/// re-analyze. Thresholds are configuration, not code: composition
/// roots can override either field via environment variables, tests
/// pass explicit instances, and the documented defaults are
/// conservative anchors only.
/// </summary>
public class StalenessPolicyEnvelopeTests
{
    private static readonly System.Collections.Generic.IReadOnlyDictionary<string, EnvelopeClassification> Classifications =
        new System.Collections.Generic.Dictionary<string, EnvelopeClassification>(System.StringComparer.Ordinal)
        {
            ["lifeblood_lookup"] = new EnvelopeClassification
            {
                TruthTier = TruthTier.Semantic,
                Confidence = ConfidenceBand.Proven,
                EvidenceSource = "Roslyn",
                Limitations = new[] { "Existing tool-specific caveat." },
            },
        };

    [Fact]
    public void Decorate_FreshGraph_DoesNotAppendStalenessLimitation()
    {
        // Zero stale seconds + zero files changed must keep the existing
        // limitations array byte-stable so non-stale paths through every
        // tool's response stay regression-free.
        var decorator = new LifebloodResponseDecorator(Classifications, StalenessPolicy.Default);

        var env = decorator.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow,
        });

        Assert.Equal(new[] { "Existing tool-specific caveat." }, env.Limitations);
    }

    [Fact]
    public void Decorate_StalenessAboveSecondsThreshold_AppendsLimitation()
    {
        // Analyze timestamp 2 hours ago + default 1-hour threshold →
        // expect a staleness-seconds limitation. The original tool
        // caveat still leads the list so consumers reading from index
        // 0 keep their previous contract.
        var twoHoursAgo = System.DateTime.UtcNow.AddSeconds(-2 * 3600);
        var decorator = new LifebloodResponseDecorator(Classifications, StalenessPolicy.Default);

        var env = decorator.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = twoHoursAgo,
        });

        Assert.True(env.Limitations.Length >= 2);
        Assert.Equal("Existing tool-specific caveat.", env.Limitations[0]);
        Assert.Contains(env.Limitations, l => l.Contains("stale"));
    }

    [Fact]
    public void Decorate_FilesChangedAboveThreshold_AppendsLimitation()
    {
        // Force the filesChanged path by handing the decorator a tracked-
        // file list whose mtimes all post-date the analyze timestamp. A
        // fake IFileSystem returns "now" for every mtime call so every
        // file counts as changed; threshold is 1 here so the limitation
        // fires.
        var analyzedAt = System.DateTime.UtcNow.AddSeconds(-60);
        var policy = new StalenessPolicy(
            StalenessSecondsWarnThreshold: long.MaxValue,
            FilesChangedWarnThreshold: 1);
        var decorator = new LifebloodResponseDecorator(Classifications, policy);

        var env = decorator.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = analyzedAt,
            TrackedFilePaths = new[] { "src/A.cs", "src/B.cs" },
            FileSystem = new AlwaysFreshFs(),
        });

        Assert.Contains(env.Limitations, l => l.Contains("tracked file"));
    }

    [Fact]
    public void Decorate_CustomThresholds_OverrideDefaults()
    {
        // Caller passes a near-zero staleness threshold → even a 30 s
        // old graph fires the limitation. Proves the policy is config,
        // not a hardcoded constant.
        var policy = new StalenessPolicy(
            StalenessSecondsWarnThreshold: 1,
            FilesChangedWarnThreshold: int.MaxValue);
        var decorator = new LifebloodResponseDecorator(Classifications, policy);

        var env = decorator.Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow.AddSeconds(-30),
        });

        Assert.Contains(env.Limitations, l => l.Contains("stale"));
    }

    [Fact]
    public void Decorate_UnregisteredTool_StillSurfacesStalenessLimitation()
    {
        // The unregistered-tool path must also honor the policy —
        // missing tool classification does not exempt the workspace
        // from the staleness contract. Both limitation entries appear:
        // the "Unregistered tool" downgrade AND the staleness entry.
        var decorator = new LifebloodResponseDecorator(Classifications, new StalenessPolicy(1, int.MaxValue));

        var env = decorator.Decorate("not_a_real_tool", new EnvelopeContext
        {
            AnalyzedAtUtc = System.DateTime.UtcNow.AddSeconds(-10),
        });

        Assert.Contains(env.Limitations, l => l.Contains("Unregistered tool"));
        Assert.Contains(env.Limitations, l => l.Contains("stale"));
    }

    private sealed class AlwaysFreshFs : Lifeblood.Application.Ports.Infrastructure.IFileSystem
    {
        public bool FileExists(string path) => true;
        public bool DirectoryExists(string path) => true;
        public string ReadAllText(string path) => string.Empty;
        public System.Collections.Generic.IEnumerable<string> ReadLines(string path) => System.Array.Empty<string>();
        public System.IO.Stream OpenRead(string path) => new System.IO.MemoryStream();
        public System.IO.Stream OpenWrite(string path) => new System.IO.MemoryStream();
        public string[] FindFiles(string directory, string pattern, bool recursive = true) => System.Array.Empty<string>();
        public System.DateTime GetLastWriteTimeUtc(string path) => System.DateTime.UtcNow;
    }
}
