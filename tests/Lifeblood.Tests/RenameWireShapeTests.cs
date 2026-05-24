using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-RENAME-POINT-EDITS-001 + INV-RENAME-CROSS-PARTIAL-001.</summary>
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

    [Fact]
    public void Rename_DiagnosticProbe_SimpleTypeRenameOnInMemoryFixture()
    {
        const string source = @"namespace Acme { public class Greeting { } }";
        using var refactoring = BuildFixture(("Greeting.cs", source));

        var edits = refactoring.Rename("type:Acme.Greeting", "Salute");

        Assert.NotEmpty(edits);
        Assert.All(edits, e => Assert.Equal("Salute", e.NewText));
    }

    [Fact]
    public void Rename_CrossPartialPrivateMethod_EmitsEditsForEveryTouchedDocument()
    {
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

        var distinctFiles = edits.Select(e => Path.GetFileName(e.FilePath ?? ""))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Host.PartA.cs", distinctFiles);
        Assert.Contains("Host.PartB.cs", distinctFiles);

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

    [Fact]
    public void Rename_SameFileMultiUseProperty_EmitsOneEditPerSite_NotWholeFile()
    {
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

        var maxSpanLines = totalLines / 2;
        Assert.All(edits, e =>
        {
            var span = e.EndLine - e.StartLine;
            Assert.True(
                span < maxSpanLines,
                $"Edit at {e.FilePath}:{e.StartLine}-{e.EndLine} spans {span} lines (totalLines={totalLines}); whole-file replacement detected.");
        });

        Assert.All(edits, e => Assert.Equal("Total", e.NewText));

        Assert.True(
            edits.Length >= 5,
            $"Expected ≥ 5 point edits for 6 logical sites (decl + 5 usages); got {edits.Length}. Wire shape may have collapsed sites.");
    }

    [Fact]
    public void Rename_SameFileMultiUseMethod_EmitsOneEditPerSite_NotWholeFile()
    {
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

        var maxSpanLines = totalLines / 2;
        Assert.All(edits, e =>
        {
            var span = e.EndLine - e.StartLine;
            Assert.True(
                span < maxSpanLines,
                $"Edit at {e.FilePath}:{e.StartLine}-{e.EndLine} spans {span} lines (totalLines={totalLines}); whole-file replacement detected.");
        });

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
            Assert.Equal("Marker", e.NewText);
            Assert.True(
                e.NewText.Length < 50,
                $"Edit NewText length {e.NewText.Length} exceeds the point-edit budget; whole-file shape detected.");
        });
    }
}
