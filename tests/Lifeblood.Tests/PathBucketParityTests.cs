using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.PathClassification;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Pin the integer-value parity between
/// <see cref="Lifeblood.Domain.PathClassification.PathBucket"/> (the
/// canonical Domain-layer classifier output) and
/// <see cref="DeadCodeBucket"/> (the Application-layer wire enum the
/// dead-code port surfaces). The dead-code analyzer relies on a direct
/// enum-to-enum cast in lieu of a switch-expression mapping; if the
/// integer values ever drift apart, every dead-code finding's
/// <c>bucket</c> wire field starts lying. This test makes the drift
/// loud at build time. INV-PATHBUCKET-SHARED-001 / LB-FOLLOWUP-20260514-005.
/// </summary>
public class PathBucketParityTests
{
    [Fact]
    public void Production_IntegerValues_Match()
        => Assert.Equal((int)PathBucket.Production, (int)DeadCodeBucket.Production);

    [Fact]
    public void Test_IntegerValues_Match()
        => Assert.Equal((int)PathBucket.Test, (int)DeadCodeBucket.Test);

    [Fact]
    public void Editor_IntegerValues_Match()
        => Assert.Equal((int)PathBucket.Editor, (int)DeadCodeBucket.Editor);

    [Fact]
    public void Generated_IntegerValues_Match()
        => Assert.Equal((int)PathBucket.Generated, (int)DeadCodeBucket.Generated);

    [Fact]
    public void MemberCount_Matches()
    {
        // Both enums must have the same number of members so any new bucket
        // added to one forces the other to follow. Off-by-one drift is the
        // most common parity break.
        var pathBucketCount = System.Enum.GetValues(typeof(PathBucket)).Length;
        var deadCodeBucketCount = System.Enum.GetValues(typeof(DeadCodeBucket)).Length;
        Assert.Equal(pathBucketCount, deadCodeBucketCount);
    }

    [Fact]
    public void NameSet_Matches()
    {
        // Defensive: also pin member NAMES so a future rename on one side
        // (e.g. "Test" → "TestCode") flags as a parity break instead of
        // silently producing a working cast with a mismatched ToString().
        // McpProvider.ClassifyBucket relies on PathBucket.ToString() to
        // produce wire-shape strings; if the names ever drift, McpProvider
        // starts emitting strings DeadCodeBucket no longer recognizes.
        var pathNames = System.Enum.GetNames(typeof(PathBucket));
        var deadCodeNames = System.Enum.GetNames(typeof(DeadCodeBucket));
        System.Array.Sort(pathNames);
        System.Array.Sort(deadCodeNames);
        Assert.Equal(pathNames, deadCodeNames);
    }
}
