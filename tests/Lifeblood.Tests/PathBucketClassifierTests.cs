using Lifeblood.Domain.PathClassification;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Direct tests for the canonical <see cref="PathBucketClassifier"/>.
/// FOLLOWUP-005 SSoT — the three former drifted impls
/// (<c>LifebloodDeadCodeAnalyzer.ClassifyBucket</c>,
/// <c>LifebloodMcpProvider.ClassifyBucket</c>,
/// <c>CircularDependencyDetector.IsGeneratedOrStaticAnalysisPath</c>)
/// now forward here. INV-PATHBUCKET-SHARED-001.
/// </summary>
public class PathBucketClassifierTests
{
    [Theory]
    [InlineData("src/Production/Foo.cs",                       PathBucket.Production)]
    [InlineData("D:\\repo\\src\\Production\\Foo.cs",           PathBucket.Production)]
    [InlineData("tests/MyTests.cs",                            PathBucket.Test)]
    [InlineData("src/foo/MyTest.cs",                           PathBucket.Test)]
    [InlineData("D:/repo/Tests/Editor/CompTests.cs",           PathBucket.Test)]
    [InlineData("Assets/Editor/Tools/Bar.cs",                  PathBucket.Editor)]
    [InlineData("D:\\proj\\Assets\\Editor\\Tools\\Bar.cs",     PathBucket.Editor)]
    [InlineData("Assets/Foo.Generated.cs",                     PathBucket.Generated)]
    [InlineData("Assets/Generated/Schema.cs",                  PathBucket.Generated)]
    [InlineData("obj/Debug/net8.0/Foo.cs",                     PathBucket.Generated)]
    [InlineData("bin/Release/Bar.cs",                          PathBucket.Generated)]
    [InlineData("src/Foo.g.cs",                                PathBucket.Generated)]
    [InlineData("",                                            PathBucket.Production)]
    public void Classify_PathPrefix_PicksMostSpecificSignal(string filePath, PathBucket expected)
        => Assert.Equal(expected, PathBucketClassifier.Classify(filePath));

    [Fact]
    public void Classify_Null_ReturnsProduction()
        => Assert.Equal(PathBucket.Production, PathBucketClassifier.Classify(null));

    [Fact]
    public void Classify_SegmentAware_NotSubstring()
    {
        // A folder called "obj-cache" must NOT trigger the Generated bucket
        // because the segment is "obj-cache", not "obj". The pre-fix
        // McpProvider impl matched on substring and got this wrong.
        Assert.Equal(PathBucket.Production, PathBucketClassifier.Classify("src/obj-cache/Foo.cs"));
        Assert.Equal(PathBucket.Production, PathBucketClassifier.Classify("src/Editorial/Foo.cs"));
        Assert.Equal(PathBucket.Production, PathBucketClassifier.Classify("src/testcase/Foo.cs"));
    }

    [Fact]
    public void Classify_GeneratedBeatsTest()
    {
        // A *.g.cs under tests/ is Generated, not Test — build artifacts
        // and codegen win every precedence battle.
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("tests/Foo.g.cs"));
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("tests/obj/Foo.cs"));
    }

    [Fact]
    public void Classify_TestBeatsEditor()
    {
        // A fixture under Tests/Editor/ is a test fixture — Tests root +
        // filename convention define what it is. The Editor/ subfolder is
        // NUnit PlayMode assembly placement, not the Editor bucket.
        Assert.Equal(PathBucket.Test, PathBucketClassifier.Classify("Tests/Editor/MyTests.cs"));
    }

    [Theory]
    [InlineData("src/Foo.g.cs",                  true)]
    [InlineData("src/Foo.Generated.cs",          true)]
    [InlineData("obj/Debug/Foo.cs",              true)]
    [InlineData("Assets/Generated/Foo.cs",       true)]
    [InlineData("src/Foo.cs",                    false)]
    [InlineData("tests/MyTests.cs",              false)]
    [InlineData("Assets/Editor/Foo.cs",          false)]
    [InlineData("",                              false)]
    public void IsGenerated_MatchesGeneratedTier(string filePath, bool expected)
        => Assert.Equal(expected, PathBucketClassifier.IsGenerated(filePath));

    [Theory]
    [InlineData("tests/MyTests.cs",              true)]
    [InlineData("src/foo/MyTest.cs",             true)]
    [InlineData("/repo/Tests/Editor/CompTests.cs", true)]
    [InlineData("src/Foo.cs",                    false)]
    [InlineData("Assets/Editor/Foo.cs",          false)]
    [InlineData("",                              false)]
    public void IsTest_MatchesTestTier(string filePath, bool expected)
        => Assert.Equal(expected, PathBucketClassifier.IsTest(filePath));
}
