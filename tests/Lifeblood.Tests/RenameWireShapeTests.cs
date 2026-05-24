using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-RENAME-POINT-EDITS-001 +
/// INV-RENAME-CROSS-PARTIAL-001 (LB-TRACK-20260524-025). The DAWG Stage 0
/// dogfood pass revealed two compound defects in <see cref="RoslynWorkspaceRefactoring.Rename"/>:
/// (1) the wire shape collapsed every document's diff into one whole-file
/// edit, defeating diff/selective-apply on the response; (2) cross-partial /
/// cross-file usages of the renamed symbol were absent from the edit list,
/// so applying the rename mechanically broke the build whenever the symbol
/// had any partial-class consumer. These tests pin the eternal posture:
/// per-TextChange point edits with narrow TextSpans, across every document
/// the renamer's Solution-scope walk touched.
///
/// Fixtures use neutral consumer vocabulary (<c>Acme</c>) — no consumer-
/// domain leakage, mirrors the discipline pinned by
/// <c>StaticTableNameLeakageTests</c>.
/// </summary>
public class RenameWireShapeTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        if (runtimeDir == null) return refs.ToArray();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        return refs.ToArray();
    }

    private static RoslynWorkspaceRefactoring BuildFixture(params (string FileName, string Source)[] files)
    {
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.FileName))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "TestModule", trees, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        // Assert compilation has no errors so test failures point at rename behavior,
        // not at fixture C# parse / bind errors.
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(diagnostics);
        return new RoslynWorkspaceRefactoring(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            {
                ["TestModule"] = compilation,
            });
    }

    // ── Sanity: does the in-memory fixture's Solution resolve the symbol at all? ──

    [Fact]
    public void Rename_DiagnosticProbe_SimpleTypeRenameOnInMemoryFixture()
    {
        // Smallest possible repro: a single type in a single file. If even this
        // returns 0 edits, the in-memory fixture itself is the issue, not
        // cross-partial / multi-use semantics. This test exists to bound the
        // failure mode for the entries below — they document REAL bugs only if
        // this one passes.
        const string source = @"namespace Acme { public class Greeting { } }";
        using var refactoring = BuildFixture(("Greeting.cs", source));

        var edits = refactoring.Rename("type:Acme.Greeting", "Salute");

        Assert.NotEmpty(edits);
        Assert.All(edits, e => Assert.Equal("Salute", e.NewText));
    }

    // ── INV-RENAME-CROSS-PARTIAL-001 ──────────────────────────────────────

    [Fact]
    public void Rename_CrossPartialPrivateMethod_EmitsEditsForEveryTouchedDocument()
    {
        // Two partial declarations of the same class. The private method lives
        // in file A; a sibling partial in file B calls it. Roslyn's
        // Renamer.RenameSymbolAsync at Solution scope MUST rewrite the call
        // site in B, and Lifeblood's adapter MUST surface that edit on the
        // wire — pre-fix only file A appeared in edits[], so mechanically
        // applying the rename left B referring to the old name.
        const string fileA = @"
namespace Acme {
  public partial class Host {
    private void Foo() { /* body */ }
  }
}";
        const string fileB = @"
namespace Acme {
  public partial class Host {
    public void Caller() { Foo(); }
  }
}";
        using var refactoring = BuildFixture(
            ("Host.PartA.cs", fileA),
            ("Host.PartB.cs", fileB));

        var edits = refactoring.Rename("method:Acme.Host.Foo()", "Bar");

        Assert.NotEmpty(edits);

        // BOTH partial files MUST appear in the edit list. The cross-partial
        // call site at PartB:Caller is the regression target.
        var distinctFiles = edits.Select(e => Path.GetFileName(e.FilePath ?? ""))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Host.PartA.cs", distinctFiles);
        Assert.Contains("Host.PartB.cs", distinctFiles);

        // Every edit must carry the new name as its body text.
        Assert.All(edits, e => Assert.Equal("Bar", e.NewText));
    }

    [Fact]
    public void Rename_CrossPartialPrivateField_EmitsEditsForEveryTouchedDocument()
    {
        const string fileA = @"
namespace Acme {
  public partial class Host {
    private int Counter = 0;
  }
}";
        const string fileB = @"
namespace Acme {
  public partial class Host {
    public int Read() => Counter;
    public void Write(int v) { Counter = v; }
  }
}";
        using var refactoring = BuildFixture(
            ("Host.FieldsA.cs", fileA),
            ("Host.FieldsB.cs", fileB));

        var edits = refactoring.Rename("field:Acme.Host.Counter", "Total");

        Assert.NotEmpty(edits);

        var distinctFiles = edits.Select(e => Path.GetFileName(e.FilePath ?? ""))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Host.FieldsA.cs", distinctFiles);
        Assert.Contains("Host.FieldsB.cs", distinctFiles);
        Assert.All(edits, e => Assert.Equal("Total", e.NewText));
    }

    // ── INV-RENAME-POINT-EDITS-001 ───────────────────────────────────────

    [Fact]
    public void Rename_SameFileMultiUseProperty_EmitsOneEditPerSite_NotWholeFile()
    {
        // Five usages + one declaration of the same property in a single file.
        // The pre-fix wire shape collapsed all six logical sites into one edit
        // spanning lines 1 → lastLine with the entire file as newText, which
        // made selective-apply impossible and overwrote any concurrent local
        // edits. Post-fix wire shape: ≥ 6 edits, each covering a narrow span
        // (single identifier, ≤ 1 line tall).
        const string source = @"
namespace Acme {
  public class Foo {
    public int Counter { get; set; }
    public int A() => Counter;
    public int B() => Counter + 1;
    public int C() => Counter * 2;
    public int D() => Counter - 1;
    public void Reset() { Counter = 0; }
  }
}";
        var totalLines = source.Count(c => c == '\n') + 1;
        using var refactoring = BuildFixture(("Foo.cs", source));

        var edits = refactoring.Rename("property:Acme.Foo.Counter", "Total");

        Assert.NotEmpty(edits);

        // Wire-shape contract: no edit may span more than a handful of lines.
        // A whole-file edit (the pre-fix shape) would have endLine - startLine
        // close to totalLines. We require every edit's vertical span to be
        // strictly less than the file size, AND aggregate edit body length to
        // be a small fraction of the file size.
        var maxSpanLines = totalLines / 2; // Generous — real point edits are ≤ 1 line.
        Assert.All(edits, e =>
        {
            var span = e.EndLine - e.StartLine;
            Assert.True(
                span < maxSpanLines,
                $"Edit at {e.FilePath}:{e.StartLine}-{e.EndLine} spans {span} lines (totalLines={totalLines}); whole-file replacement detected.");
        });

        // Every edit's body must be exactly the new name (no surrounding
        // file body re-emission). One renamed identifier per edit.
        Assert.All(edits, e => Assert.Equal("Total", e.NewText));

        // We expect ≥ 6 edits (1 decl + 5 usages). Roslyn dedup quirks could
        // collapse adjacent identical changes; allow 5 as a floor but flag
        // anything that collapses below the per-site granularity.
        Assert.True(
            edits.Length >= 5,
            $"Expected ≥ 5 point edits for 6 logical sites (decl + 5 usages); got {edits.Length}. Wire shape may have collapsed sites.");
    }

    [Fact]
    public void Rename_SameFileMultiUseMethod_EmitsOneEditPerSite_NotWholeFile()
    {
        // Roslyn's Document.GetTextChangesAsync emits MINIMAL diffs — for an
        // old/new pair like `Compute` → `Calculate`, the change is the
        // differing-substring span (e.g. `ompu` → `alcula`), not the entire
        // identifier. That's even more granular than the per-site contract;
        // the wire-shape requirement is "narrow span per edit", not "newText
        // equals new name". The point-edit contract is satisfied as long as
        // each edit's span and text payload stay bounded by the rename's
        // identifier diff, not by the file body.
        const string source = @"
namespace Acme {
  public class Foo {
    public int Compute(int x) { return x * 2; }
    public int A() => Compute(1);
    public int B() => Compute(2);
    public int C() => Compute(3);
    public int D() => Compute(4);
  }
}";
        var totalLines = source.Count(c => c == '\n') + 1;
        using var refactoring = BuildFixture(("Foo.cs", source));

        var edits = refactoring.Rename("method:Acme.Foo.Compute(int)", "Calculate");

        Assert.NotEmpty(edits);

        // Every edit must occupy a NARROW vertical span (point-edit
        // contract). Whole-file replacement would span totalLines.
        var maxSpanLines = totalLines / 2;
        Assert.All(edits, e =>
        {
            var span = e.EndLine - e.StartLine;
            Assert.True(
                span < maxSpanLines,
                $"Edit at {e.FilePath}:{e.StartLine}-{e.EndLine} spans {span} lines (totalLines={totalLines}); whole-file replacement detected.");
        });

        // Every edit's body must be SHORTER than the new identifier — Roslyn
        // emits minimal text diff, so the replacement payload is ≤ the new
        // identifier's length. Whole-file shape would carry hundreds of chars.
        Assert.All(edits, e => Assert.True(
            e.NewText.Length <= "Calculate".Length,
            $"Edit NewText length {e.NewText.Length} exceeds the new identifier; whole-file shape may be re-emitting source body."));

        Assert.True(
            edits.Length >= 4,
            $"Expected ≥ 4 point edits for 5 logical sites (decl + 4 usages); got {edits.Length}.");
    }

    [Fact]
    public void Rename_PointEditNewText_ContainsOnlyTheNewIdentifier_NoFileBody()
    {
        // Strongest pre-fix detection: the whole-file shape's newText carried
        // the entire file content. A correct point edit's newText must be
        // shorter than even one line of surrounding code — it's the
        // identifier only, nothing else.
        const string source = @"
namespace Acme {
  public class Foo {
    public int Sentinel { get; set; }
    public int Read() => Sentinel;
  }
}";
        using var refactoring = BuildFixture(("Foo.cs", source));

        var edits = refactoring.Rename("property:Acme.Foo.Sentinel", "Marker");

        Assert.NotEmpty(edits);
        Assert.All(edits, e =>
        {
            // The new name is "Marker" — every edit's NewText must equal it
            // exactly. The whole-file shape would carry the full source body
            // (with the rename baked in) as NewText, which is hundreds of
            // characters long.
            Assert.Equal("Marker", e.NewText);
            Assert.True(
                e.NewText.Length < 50,
                $"Edit NewText length {e.NewText.Length} exceeds the point-edit budget; whole-file shape detected.");
        });
    }
}
