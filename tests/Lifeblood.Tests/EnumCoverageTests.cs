using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-ENUM-COVERAGE-001 —
/// <see cref="ICompilationHost.GetEnumCoverage"/> classifies each enum
/// member reference site as Produced / ConsumedComparison /
/// ConsumedSwitch / Other so a caller can answer "is this value ever
/// produced?" in one call. Closes LB-TRACK-20260514-003.
/// </summary>
public class EnumCoverageTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static RoslynCompilationHost HostWith(string source, string fileName = "Test.cs", string moduleName = "Test")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CSharpCompilation.Create(
            moduleName, new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [moduleName] = compilation,
        });
    }

    [Fact]
    public void GetEnumCoverage_NonEnumType_ReturnsNull()
    {
        using var host = HostWith("namespace Acme { public class Foo { } }");

        Assert.Null(host.GetEnumCoverage("type:Acme.Foo"));
    }

    [Fact]
    public void GetEnumCoverage_UnknownType_ReturnsNull()
    {
        using var host = HostWith("namespace Acme { public enum Mode { A } }");

        Assert.Null(host.GetEnumCoverage("type:Acme.DoesNotExist"));
    }

    [Fact]
    public void GetEnumCoverage_EmptyEnum_ZeroMembers()
    {
        using var host = HostWith("namespace Acme { public enum Empty { } }");

        var report = host.GetEnumCoverage("type:Acme.Empty");

        Assert.NotNull(report);
        Assert.Equal("Empty", report!.EnumTypeName);
        Assert.Empty(report.Members);
        Assert.Equal(0, report.UnproducedCount);
        Assert.Equal(0, report.UnreferencedCount);
    }

    [Fact]
    public void GetEnumCoverage_ProductionSites_CountedAsProduced()
    {
        // A used in: variable initializer + return + assignment +
        // method argument. All four are production sites — value flows
        // into a receiver.
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public class Host {
    Mode _field;
    Mode Initialize() {
      var local = Mode.A;
      _field = Mode.A;
      Use(Mode.A);
      return Mode.A;
    }
    void Use(Mode m) { }
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members, m => m.Name == "A");
        Assert.Equal(4, a.ProducedCount);
        Assert.Equal(0, a.ConsumedComparisonCount);
        Assert.Equal(0, a.ConsumedSwitchCount);
        Assert.False(a.IsUnproduced);
        Assert.False(a.IsUnreferenced);
    }

    [Fact]
    public void GetEnumCoverage_ComparisonSites_CountedAsConsumedComparison()
    {
        // Equality and inequality both bucket as ConsumedComparison.
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public class Host {
    bool Check(Mode m) {
      if (m == Mode.A) return true;
      if (m != Mode.A) return false;
      return false;
    }
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        var a = Assert.Single(report!.Members, m => m.Name == "A");
        Assert.Equal(0, a.ProducedCount);
        Assert.Equal(2, a.ConsumedComparisonCount);
        Assert.Equal(0, a.ConsumedSwitchCount);
        Assert.True(a.IsUnproduced);
        Assert.False(a.IsUnreferenced);
    }

    [Fact]
    public void GetEnumCoverage_SwitchAndPattern_CountedAsConsumedSwitch()
    {
        // `case Mode.A:`, switch-expression arm, and `is` constant
        // pattern all bucket as ConsumedSwitch.
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public class Host {
    int Score(Mode m) {
      switch (m) { case Mode.A: return 1; default: return 0; }
    }
    int ScoreExpr(Mode m) => m switch { Mode.A => 1, _ => 0 };
    bool Is(Mode m) => m is Mode.A;
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        var a = Assert.Single(report!.Members, m => m.Name == "A");
        Assert.Equal(0, a.ProducedCount);
        Assert.Equal(0, a.ConsumedComparisonCount);
        Assert.Equal(3, a.ConsumedSwitchCount);
        Assert.True(a.IsUnproduced);
        Assert.False(a.IsUnreferenced);
    }

    [Fact]
    public void GetEnumCoverage_UnproducedValue_FlaggedAndCounted()
    {
        // The dogfood case: B is consumed (switch arm) but never
        // produced. The state-machine drift signal — a value exists in
        // the type, is checked for, but is never assigned to anything.
        const string source = @"
namespace Acme {
  public enum Mode { A, B, C }
  public class Host {
    void Produce() { var x = Mode.A; }
    int Consume(Mode m) {
      switch (m) { case Mode.B: return 1; default: return 0; }
    }
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var b = Assert.Single(report!.Members, m => m.Name == "B");
        Assert.True(b.IsUnproduced);
        Assert.False(b.IsUnreferenced);
        var c = Assert.Single(report.Members, m => m.Name == "C");
        Assert.True(c.IsUnreferenced);
        Assert.False(c.IsUnproduced); // IsUnproduced requires TotalReferences > 0
        Assert.Equal(1, report.UnproducedCount); // just B
        Assert.Equal(1, report.UnreferencedCount); // just C
    }

    [Fact]
    public void GetEnumCoverage_MembersAreInDeclarationOrder()
    {
        // Caller-friendly: rows appear in source declaration order so
        // a wire-serialized table reads top-to-bottom in the same order
        // a human sees them in the enum body.
        using var host = HostWith("namespace Acme { public enum Mode { Zebra, Alpha, Mango } }");

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        Assert.Equal(new[] { "Zebra", "Alpha", "Mango" },
            report!.Members.Select(m => m.Name).ToArray());
    }
}
