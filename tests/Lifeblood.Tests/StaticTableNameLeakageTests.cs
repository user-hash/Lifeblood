using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-EXTRACT-STATIC-TABLES-001 ratchet — the static-table extractor
/// is generic Roslyn semantic tooling and MUST NOT leak any
/// consumer-domain vocabulary into its source. The forbidden list
/// below curates project-specific identifiers from real-world
/// consumer codebases that have no business in a generic extractor —
/// any match anywhere in the extractor or its DTOs fails the build.
/// Add new tokens here whenever a new downstream consumer ships;
/// removing tokens requires written justification because each was
/// added in response to a real drift signal.
/// </summary>
public class StaticTableNameLeakageTests
{
    /// <summary>
    /// Files scanned for consumer-domain leakage. Scope is the
    /// extractor + its Domain DTOs only — tests use neutral
    /// <c>Acme</c> / <c>Foo</c> fixtures by design and are out of
    /// scope here.
    /// </summary>
    private static readonly string[] FilesUnderRatchet =
    {
        "src/Lifeblood.Adapters.CSharp/RoslynStaticTableExtractor.cs",
        "src/Lifeblood.Domain/Results/StaticTableResults.cs",
    };

    /// <summary>
    /// Curated consumer-domain identifiers. Drawn from real downstream
    /// projects this tool has dogfooded against — every token here
    /// would betray that the extractor was specialised for one
    /// consumer's row shape. Case-insensitive match.
    /// </summary>
    private static readonly string[] ForbiddenTokens =
    {
        "DAWG",
        "Nebulae",
        "KernelCapability",
        "KernelFeature",
        "BurstKernel",
        "BurstSampleMixer",
        "FieldMask",
        "BeatGrid",
        "AdaptiveBeatGrid",
        "MovingState",
        "ShimmerPhase",
    };

    [Fact]
    public void Extractor_DoesNotReferenceConsumerDomainVocabulary()
    {
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);

        var leaks = new List<string>();
        foreach (var relative in FilesUnderRatchet)
        {
            var path = Path.Combine(repoRoot!, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Ratchet scope file missing: {relative}");
            var content = File.ReadAllText(path);
            foreach (var token in ForbiddenTokens)
            {
                if (content.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    leaks.Add($"{relative} :: {token}");
            }
        }

        Assert.Empty(leaks);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Lifeblood.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
