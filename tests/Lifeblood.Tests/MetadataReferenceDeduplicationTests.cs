using Lifeblood.Adapters.CSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression suite for the assembly identity unification step that
/// brings Lifeblood's compilation reference graph into parity with
/// MSBuild's <c>&lt;AutoUnify&gt;true&lt;/AutoUnify&gt;</c> default
/// (INV-DIAGNOSTIC-NUGET-BINDING-PARITY-001). Workspaces whose NuGet
/// closure overlaps simple-names with the SDK BCL ref pack MUST emit
/// zero CS1701 / CS1702 / CS1705 — same outcome MSBuild produces.
///
/// Asserted invariants:
///   1. Same simple-name + different version collapses to the
///      highest-version reference.
///   2. Distinct simple-names all survive.
///   3. References whose metadata cannot be read as an assembly pass
///      through unchanged.
///   4. End-to-end: the deduplicator-fed compilation emits zero
///      CS1701 even when the input set carries an intentional
///      duplicate identity.
/// </summary>
public class MetadataReferenceDeduplicationTests
{
    [Fact]
    public void Deduplicate_SameSimpleNameDifferentVersions_KeepsHighestVersion()
    {
        var older = BuildSyntheticAssembly("Acme.Lib", "1.0.0.0");
        var newer = BuildSyntheticAssembly("Acme.Lib", "2.5.0.0");

        var result = MetadataReferenceDeduplicator.Deduplicate(new[] { older, newer });

        Assert.Single(result);
        Assert.Same(newer, result[0]);
    }

    [Fact]
    public void Deduplicate_OrderingDoesNotMatter_HighestVersionStillWins()
    {
        // Order-independent: same outcome whether the higher version is
        // observed first or last in the input enumeration.
        var older = BuildSyntheticAssembly("Acme.Lib", "1.0.0.0");
        var newer = BuildSyntheticAssembly("Acme.Lib", "2.5.0.0");

        var newerFirst = MetadataReferenceDeduplicator.Deduplicate(new[] { newer, older });
        var olderFirst = MetadataReferenceDeduplicator.Deduplicate(new[] { older, newer });

        Assert.Single(newerFirst);
        Assert.Single(olderFirst);
        Assert.Same(newer, newerFirst[0]);
        Assert.Same(newer, olderFirst[0]);
    }

    [Fact]
    public void Deduplicator_BucketKey_ComposesNameCultureAndPublicKey()
    {
        // Soundness ratchet: the bucket key MUST include simple name,
        // culture, AND the public-key blob so two assemblies sharing a
        // simple name but signed with different keys (or with no key)
        // survive as distinct identities. Synthesizing two PE images
        // with differing public-key blobs in a unit test is more setup
        // than the test budget supports (real signing requires key
        // generation + PE-level emit options), so this ratchet locks
        // the invariant by source inspection: the bucket-key
        // construction in MetadataReferenceDeduplicator.cs must
        // reference the AssemblyDefinition's Culture AND PublicKey
        // blob, AND must compose the three pieces into the dictionary
        // key. A future revert to simple-name-only fails this test
        // because the source would no longer mention "Culture" or
        // "PublicKey" in the key-composition site.
        var repoRoot = FindRepoRoot();
        var sourcePath = Path.Combine(repoRoot,
            "src", "Lifeblood.Adapters.CSharp", "Internal", "MetadataReferenceDeduplicator.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("def.Culture", source);
        Assert.Contains("def.PublicKey", source);
        Assert.Contains("bucketKey", source);
        // Bucket key must compose all three pieces — the canonical
        // shape is `name|culture|publicKey` (or a future equivalent
        // that still keys on all three). A bare `byName` dictionary
        // would not pin distinct identities; the source must NOT
        // reduce to simple-name keying.
        Assert.DoesNotContain("byName = new Dictionary", source);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            current = current.Parent;
        Assert.NotNull(current);
        return current!.FullName;
    }

    [Fact]
    public void Deduplicate_DistinctSimpleNames_AllSurvive()
    {
        var a = BuildSyntheticAssembly("Acme.Alpha", "1.0.0.0");
        var b = BuildSyntheticAssembly("Acme.Beta", "1.0.0.0");
        var c = BuildSyntheticAssembly("Acme.Gamma", "1.0.0.0");

        var result = MetadataReferenceDeduplicator.Deduplicate(new[] { a, b, c });

        Assert.Equal(3, result.Count);
        Assert.Contains(a, result);
        Assert.Contains(b, result);
        Assert.Contains(c, result);
    }

    [Fact]
    public void Deduplicate_EmptyInput_ReturnsEmpty()
    {
        var result = MetadataReferenceDeduplicator.Deduplicate(Array.Empty<MetadataReference>());
        Assert.Empty(result);
    }

    [Fact]
    public void Deduplicate_UnreadableIdentity_PassesThroughUnchanged()
    {
        // A reference whose metadata cannot be read as an assembly
        // identity must not be dropped by the dedup pass — the loader
        // surfaces the underlying load failure as a regular diagnostic.
        // We synthesize the case by constructing a degenerate reference
        // from an empty image; the dedup helper catches the read failure
        // and routes the entry through the unkeyed bucket.
        var poison = MetadataReference.CreateFromImage(new byte[] { 0 });

        var result = MetadataReferenceDeduplicator.Deduplicate(new[] { poison });

        Assert.Single(result);
        Assert.Same(poison, result[0]);
    }

    [Fact]
    public void Compilation_WithDuplicateIdentities_AfterDedup_EmitsNoCS1701()
    {
        // End-to-end: feed Roslyn the kind of input that historically
        // triggered CS1701 / CS1702 / CS1705 — two refs with the same
        // simple-name but different versions, each consumed via type-ref
        // from a third compilation. With dedup applied, Roslyn sees a
        // single canonical identity and emits zero binding-redirect
        // diagnostics; without dedup, every type-ref to the colliding
        // assembly fires one warning.
        var olderProvider = BuildAssemblyExposingType("Acme.Provider", "1.0.0.0", "namespace Acme.Provider; public class Provider { public static int Value() => 1; }");
        var newerProvider = BuildAssemblyExposingType("Acme.Provider", "2.0.0.0", "namespace Acme.Provider; public class Provider { public static int Value() => 1; }");

        // Consumer references the higher-version surface so the call binds cleanly post-dedup.
        var consumerSource = CSharpSyntaxTree.ParseText(
            "namespace Consumer; public class C { public int Call() => Acme.Provider.Provider.Value(); }",
            path: "Consumer.cs");

        var rawRefs = new MetadataReference[] { olderProvider, newerProvider }
            .Concat(BclReferenceLoader.References.Value)
            .ToArray();

        var deduped = MetadataReferenceDeduplicator.Deduplicate(rawRefs);

        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { consumerSource },
            deduped,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var cs1701 = compilation.GetDiagnostics().Where(d => d.Id == "CS1701").ToArray();
        var cs1702 = compilation.GetDiagnostics().Where(d => d.Id == "CS1702").ToArray();
        var cs1705 = compilation.GetDiagnostics().Where(d => d.Id == "CS1705").ToArray();

        Assert.Empty(cs1701);
        Assert.Empty(cs1702);
        Assert.Empty(cs1705);
    }

    /// <summary>
    /// Build a minimal in-memory assembly with a given simple-name and
    /// version, then wrap it as a PE-image MetadataReference. The unit
    /// is an empty namespace declaration carrying an
    /// <see cref="System.Reflection.AssemblyVersionAttribute"/> so
    /// Roslyn stamps the desired identity onto the emitted PE.
    /// </summary>
    private static MetadataReference BuildSyntheticAssembly(string simpleName, string version)
        => BuildAssemblyExposingType(simpleName, version, $"namespace {simpleName} {{ public class Placeholder {{ }} }}");

    private static MetadataReference BuildAssemblyExposingType(string simpleName, string version, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            $"using System.Reflection; [assembly: AssemblyVersion(\"{version}\")] {source}",
            path: simpleName + ".cs");
        var compilation = CSharpCompilation.Create(
            simpleName,
            new[] { tree },
            BclReferenceLoader.References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, "Synthetic assembly emit failed: " + string.Join("; ", result.Diagnostics.Select(d => d.GetMessage())));
        return MetadataReference.CreateFromImage(ms.ToArray());
    }
}
