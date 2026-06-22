using System;

namespace Lifeblood.Domain.PathClassification;

/// <summary>
/// Path-prefix taxonomy. Single shared SSoT for path-bucket classification
/// across dead-code analysis, blast-radius grouping, and cycle taxonomy.
/// Integer values are stable (Production=0, Test=1, Editor=2, Generated=3, Vendored=4)
/// so wire-shape enums (e.g. <c>DeadCodeBucket</c> in Application) can
/// declare matching values and rely on a same-int cast — pinned by
/// <c>PathBucketParityTests</c>. INV-PATHBUCKET-SHARED-001.
/// </summary>
public enum PathBucket
{
    Production = 0,
    Test = 1,
    Editor = 2,
    Generated = 3,
    Vendored = 4,
}

/// <summary>
/// Single canonical path-bucket classifier. Replaces three drifted
/// implementations called out in <c>INV-CYCLE-TAXONOMY-001</c>:
///   * <c>LifebloodDeadCodeAnalyzer.ClassifyBucket</c> — segment-aware path buckets.
///   * <c>LifebloodMcpProvider.ClassifyBucket</c> — substring-based, string return,
///     additional <c>*.g.cs</c> generated-suffix support.
///   * <c>CircularDependencyDetector.IsGeneratedOrStaticAnalysisPath</c> —
///     Generated-only predicate.
///
/// Segment-aware on the lowercase POSIX-normalized form so Windows and
/// POSIX inputs collapse to one match table. Precedence (most authoritative
/// signal wins): Generated &gt; Vendored &gt; Test &gt; Editor &gt; Production.
///
/// Generated: filename ends <c>*.g.cs</c> / <c>*.generated.cs</c>, or
///   contains <c>.generated.</c>, or any path segment is
///   <c>generated</c> / <c>obj</c> / <c>bin</c>. Build artifacts and
///   codegen output are never refactor targets regardless of any other
///   path signal.
///
/// Vendored: common third-party / sample roots such as <c>Packages</c>,
///   <c>PackageCache</c>, <c>Samples~</c>, <c>Examples*</c>,
///   <c>ThirdParty</c>, <c>External</c>, or <c>Vendor</c>. Vendored beats
///   Test and Editor so package fixtures and editor utilities fold with
///   their owning third-party tree.
///
/// Test: any path segment is <c>tests</c>, or filename ends
///   <c>Tests.cs</c> / <c>Test.cs</c>. Beats Editor in precedence — a
///   fixture under <c>Tests/Editor/Foo.cs</c> is a test fixture (Tests
///   root + filename convention define what it is); the <c>Editor/</c>
///   subfolder is just NUnit PlayMode assembly placement.
///
/// Editor: any path segment is <c>editor</c>. Unity editor-only utility,
///   excluded from runtime builds.
///
/// Production: default. Normal runtime code.
///
/// INV-PATHBUCKET-SHARED-001 / LB-FOLLOWUP-20260514-005.
/// </summary>
public static class PathBucketClassifier
{
    /// <summary>Classify a file path into the path bucket. Null/empty maps to Production.</summary>
    public static PathBucket Classify(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return PathBucket.Production;
        var lower = filePath.Replace('\\', '/').ToLowerInvariant();
        var segments = lower.Split('/');

        if (IsGeneratedShape(lower, segments)) return PathBucket.Generated;
        if (IsVendoredShape(segments))         return PathBucket.Vendored;
        if (IsTestShape(lower, segments))      return PathBucket.Test;
        if (IsEditorShape(segments))           return PathBucket.Editor;
        return PathBucket.Production;
    }

    /// <summary>True iff the path classifies as Generated. Convenience for
    /// callers that only need the Generated/non-Generated split (e.g.
    /// cycle-taxonomy first-bucket short-circuit).</summary>
    public static bool IsGenerated(string? filePath)
        => Classify(filePath) == PathBucket.Generated;

    /// <summary>True iff the path classifies as Test (segment <c>tests</c>
    /// or filename ends <c>Tests.cs</c>/<c>Test.cs</c>). Convenience for
    /// callers that only need the Test/non-Test split (e.g. ExcludeTests
    /// dead-code filter).</summary>
    public static bool IsTest(string? filePath)
        => Classify(filePath) == PathBucket.Test;

    /// <summary>True iff the path classifies as Vendored.</summary>
    public static bool IsVendored(string? filePath)
        => Classify(filePath) == PathBucket.Vendored;

    private static bool IsGeneratedShape(string lower, string[] segments)
    {
        if (lower.EndsWith(".g.cs", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".generated.cs", StringComparison.Ordinal)) return true;
        if (lower.Contains(".generated.")) return true;
        foreach (var s in segments)
            if (s == "obj" || s == "bin" || s == "generated") return true;
        return false;
    }

    private static bool IsTestShape(string lower, string[] segments)
    {
        foreach (var s in segments)
            if (s == "tests") return true;
        if (lower.EndsWith("tests.cs", StringComparison.Ordinal)) return true;
        if (lower.EndsWith("test.cs", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsVendoredShape(string[] segments)
    {
        foreach (var s in segments)
        {
            if (s == "packages") return true;
            if (s == "packagecache") return true;
            if (s == "samples" || s == "samples~" || s == "sample") return true;
            if (s.StartsWith("examples", StringComparison.Ordinal)) return true;
            if (s == "thirdparty" || s == "third-party" || s == "third_party") return true;
            if (s == "vendor" || s == "vendored" || s == "external") return true;
        }
        return false;
    }

    private static bool IsEditorShape(string[] segments)
    {
        foreach (var s in segments)
            if (s == "editor") return true;
        return false;
    }
}
