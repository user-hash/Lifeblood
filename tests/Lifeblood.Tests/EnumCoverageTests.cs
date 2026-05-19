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

    // ──────────────────────────────────────────────────────────────────
    // S4: dispatch-table reference coverage. ADDITIVE — the same
    // reference still counts under its syntactic bucket (typically
    // ProducedCount) AND increments DispatchTableReferenceCount when
    // the enclosing context is a static-table-shaped initializer. The
    // recognition logic is shared with the static_tables tool via
    // RoslynStaticTableExtractor.IsInsideStaticTableInitializer —
    // single source of truth for "what counts as a static-table-shaped
    // initializer". INV-ENUM-COVERAGE-DISPATCH-TABLE-001.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GetEnumCoverage_DispatchTableObjectCreationRow_CountsAsDispatchTable()
    {
        // ABG-style: static array of constructed rows. Mode.A is the
        // routing key in `new(Mode.A, HandleA)`; pre-S4 the only signal
        // was ProducedCount=1 with no way to distinguish "routing key"
        // from "actually produced".
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public sealed class Capability {
    public Capability(Mode m, System.Action h) { Key = m; Handler = h; }
    public Mode Key { get; }
    public System.Action Handler { get; }
  }
  public static class Registry {
    public static void HandleA() { }
    public static void HandleB() { }
    public static readonly Capability[] All = new[] {
      new Capability(Mode.A, HandleA),
      new Capability(Mode.B, HandleB),
    };
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members, m => m.Name == "A");
        var b = Assert.Single(report.Members, m => m.Name == "B");

        // Additive: each dispatch-table cell increments BOTH Produced
        // (argument position is syntactically value-producing) AND the
        // new DispatchTableReferenceCount.
        Assert.Equal(1, a.ProducedCount);
        Assert.Equal(1, a.DispatchTableReferenceCount);
        Assert.Equal(1, b.ProducedCount);
        Assert.Equal(1, b.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_RegularProductionSite_DoesNotCountAsDispatchTable()
    {
        // Backward-compat: a normal assignment outside any static-table
        // initializer must NOT increment DispatchTableReferenceCount.
        const string source = @"
namespace Acme {
  public enum Mode { A }
  public class Host {
    Mode _m;
    void Set() { _m = Mode.A; }
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members);
        Assert.Equal(1, a.ProducedCount);
        Assert.Equal(0, a.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_LiteralArrayInitializer_CountsAsDispatchTable()
    {
        // Plain-typed static array of enum literals — implicit-array shape
        // (`Mode[] X = { Mode.A, Mode.B }`). Each element is a dispatch-table
        // cell at the recognition layer.
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public static class Routes {
    public static readonly Mode[] All = { Mode.A, Mode.B };
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members, m => m.Name == "A");
        var b = Assert.Single(report.Members, m => m.Name == "B");
        Assert.Equal(1, a.DispatchTableReferenceCount);
        Assert.Equal(1, b.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_SwitchArm_DoesNotCountAsDispatchTable()
    {
        // Switch consumption is in method body, not a static initializer.
        // Backward-compat: ConsumedSwitchCount unchanged, dispatch counter zero.
        const string source = @"
namespace Acme {
  public enum Mode { A }
  public class Host {
    int Consume(Mode m) {
      switch (m) { case Mode.A: return 1; default: return 0; }
    }
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members);
        Assert.Equal(1, a.ConsumedSwitchCount);
        Assert.Equal(0, a.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_ExpressionBodiedStaticProperty_CountsAsDispatchTable()
    {
        // Parity with static_tables tool: TryGetInitializerExpression
        // recognises BOTH EqualsValueClauseSyntax (= expr) AND
        // ArrowExpressionClauseSyntax (=> expr) on static properties.
        // The recognition predicate must match.
        const string source = @"
namespace Acme {
  public enum Mode { A }
  public static class Routes {
    public static Mode[] All => new[] { Mode.A };
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members);
        Assert.Equal(1, a.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_LambdaInsideStaticInitializer_DoesNotCountAsDispatchTable()
    {
        // Edge case: a lambda body inside a static initializer is its
        // own scope. `Mode.A` inside the lambda body is NOT a dispatch-
        // table cell; the lambda is. Predicate must stop at the
        // AnonymousFunctionExpressionSyntax boundary.
        const string source = @"
namespace Acme {
  public enum Mode { A }
  public static class Reg {
    public static readonly System.Func<Mode> Get = () => Mode.A;
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members);
        Assert.Equal(0, a.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_NonStaticFieldInitializer_DoesNotCountAsDispatchTable()
    {
        // Recognition predicate must reject INSTANCE field initializers.
        // Same array shape, but no `static` modifier → not a dispatch
        // table at the static_tables tool's recognition layer.
        const string source = @"
namespace Acme {
  public enum Mode { A }
  public class Host {
    private readonly Mode[] _routes = { Mode.A };
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members);
        Assert.Equal(1, a.ProducedCount);
        Assert.Equal(0, a.DispatchTableReferenceCount);
    }

    [Fact]
    public void GetEnumCoverage_DispatchTableOnly_MemberIsTriageableFromOneCall()
    {
        // The S4 use case: Mode.B's ONLY reference is a dispatch-table
        // cell. A caller can read off the row and see ProducedCount=1
        // AND DispatchTableReferenceCount=1 (so the value is ONLY a
        // routing key, never genuinely produced in app code) — the
        // state-machine triage signal the F3-series pattern asks for.
        const string source = @"
namespace Acme {
  public enum Mode { A, B }
  public sealed class Cap { public Cap(Mode m) { K = m; } public Mode K { get; } }
  public static class Reg {
    public static readonly Cap[] All = new[] { new Cap(Mode.B) };
  }
  public class App {
    void Use() { var m = Mode.A; var x = m; } // A produced normally
  }
}";
        using var host = HostWith(source);

        var report = host.GetEnumCoverage("type:Acme.Mode");

        Assert.NotNull(report);
        var a = Assert.Single(report!.Members, m => m.Name == "A");
        var b = Assert.Single(report.Members, m => m.Name == "B");

        // A: produced normally, never in a dispatch table.
        Assert.Equal(1, a.ProducedCount);
        Assert.Equal(0, a.DispatchTableReferenceCount);

        // B: only reference is the dispatch-table cell. Both counters
        // ring — the additive design lets one row answer "is this value
        // only a routing key?" via DispatchTable >= Produced.
        Assert.Equal(1, b.ProducedCount);
        Assert.Equal(1, b.DispatchTableReferenceCount);
        Assert.Equal(b.ProducedCount, b.DispatchTableReferenceCount);
    }
}
