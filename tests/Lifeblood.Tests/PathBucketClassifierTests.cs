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
    [InlineData("Packages/com.vendor.tool/Runtime/Foo.cs",     PathBucket.Vendored)]
    [InlineData("Library/PackageCache/com.foo@1.0/Bar.cs",     PathBucket.Vendored)]
    [InlineData("Assets/TextMesh Pro/Examples & Extras/Demo.cs", PathBucket.Vendored)]
    [InlineData("Assets/Samples~/Pack/Demo.cs",                PathBucket.Vendored)]
    [InlineData("Assets/ThirdParty/Plugin/Foo.cs",             PathBucket.Vendored)]
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
        Assert.Equal(PathBucket.Production, PathBucketClassifier.Classify("src/PackageComposer/Foo.cs"));
    }

    [Fact]
    public void Classify_GeneratedBeatsTest()
    {
        // A *.g.cs under tests/ is Generated, not Test — build artifacts
        // and codegen win every precedence battle.
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("tests/Foo.g.cs"));
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("tests/obj/Foo.cs"));
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("Packages/com.foo/Generated/Foo.cs"));
        Assert.Equal(PathBucket.Generated, PathBucketClassifier.Classify("Assets/TextMesh Pro/Examples & Extras/Foo.g.cs"));
    }

    [Fact]
    public void Classify_VendoredBeatsTestAndEditor()
    {
        Assert.Equal(PathBucket.Vendored, PathBucketClassifier.Classify("Packages/com.foo/Tests/FooTests.cs"));
        Assert.Equal(PathBucket.Vendored, PathBucketClassifier.Classify("Assets/TextMesh Pro/Examples & Extras/Editor/Tool.cs"));
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
    [InlineData("Packages/com.foo/Tests/FooTests.cs", false)]
    [InlineData("",                              false)]
    public void IsTest_MatchesTestTier(string filePath, bool expected)
        => Assert.Equal(expected, PathBucketClassifier.IsTest(filePath));

    [Theory]
    [InlineData("Packages/com.vendor.tool/Runtime/Foo.cs", true)]
    [InlineData("Library/PackageCache/com.foo@1.0/Bar.cs", true)]
    [InlineData("Assets/TextMesh Pro/Examples & Extras/Demo.cs", true)]
    [InlineData("Assets/Samples~/Pack/Demo.cs", true)]
    [InlineData("Assets/External/Plugin/Foo.cs", true)]
    [InlineData("src/Foo.cs", false)]
    [InlineData("src/PackageComposer/Foo.cs", false)]
    [InlineData("", false)]
    public void IsVendored_MatchesVendoredTier(string filePath, bool expected)
        => Assert.Equal(expected, PathBucketClassifier.IsVendored(filePath));

    [Fact]
    public void PathGlobMatcher_MatchesFullNormalizedPosixPath()
    {
        var globs = PathGlobMatcher.Compile(new[] { "*/Examples*/*", "Packages/*" });

        Assert.True(PathGlobMatcher.MatchesAny(globs, @"Assets\TextMesh Pro\Examples & Extras\Demo.cs"));
        Assert.True(PathGlobMatcher.MatchesAny(globs, "Packages/com.vendor/Foo.cs"));
        Assert.False(PathGlobMatcher.MatchesAny(globs, "src/FooExamplesConsumer/Foo.cs"));
    }
}
