using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-EXTRACT-STATIC-TABLES-001 —
/// <see cref="ICompilationHost.GetStaticTables"/> classifies static
/// field / property initializers as <see cref="StaticTableReport"/>
/// rows + cells sourced from <c>SemanticModel.GetOperation</c>. Every
/// fixture uses neutral consumer vocabulary (<c>Acme</c>, <c>Color</c>,
/// <c>Token</c>, <c>Mode</c>) — INV requires the extractor be
/// consumer-domain-blind. Pinned alongside the
/// <c>NoConsumerLeakage</c> ratchet.
/// </summary>
public class StaticTableExtractorTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
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

    private static StaticTablesOptions Default => new();

    [Fact]
    public void GetStaticTables_UnknownType_ReturnsNull()
    {
        using var host = HostWith("namespace Acme { public class Foo { } }");

        Assert.Null(host.GetStaticTables("type:Acme.DoesNotExist", Default));
    }

    [Fact]
    public void GetStaticTables_TypeWithoutStaticInitializers_EmptyTables()
    {
        using var host = HostWith("namespace Acme { public class Foo { public int Instance = 1; } }");

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        Assert.NotNull(report);
        Assert.Equal("type:Acme.Foo", report!.TypeId);
        Assert.Empty(report.Tables);
        Assert.False(report.TablesTruncated);
    }

    [Fact]
    public void GetStaticTables_StaticArrayField_DetectedAsArrayContainer()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] Numbers = new int[] { 1, 2, 3 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        Assert.NotNull(report);
        var table = Assert.Single(report!.Tables);
        Assert.Equal("Numbers", table.MemberName);
        Assert.StartsWith("field:", table.MemberId);
        Assert.Equal(StaticTableContainerKind.Array, table.ContainerKind);
        Assert.Equal(3, table.Rows.Length);
        Assert.False(table.RowsTruncated);
    }

    [Fact]
    public void GetStaticTables_StaticCollectionExpression_DetectedAsCollectionExpression()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] Numbers = [ 10, 20, 30, 40 ];
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        var table = Assert.Single(report!.Tables);
        Assert.Equal(StaticTableContainerKind.CollectionExpression, table.ContainerKind);
        Assert.Equal(4, table.Rows.Length);
    }

    [Fact]
    public void GetStaticTables_StaticObjectCreation_DetectedAsObjectCreationSingleRow()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int x) { } }
  public class Foo {
    public static readonly Row Default = new Row(7);
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        var table = Assert.Single(report!.Tables);
        Assert.Equal("Default", table.MemberName);
        Assert.Equal(StaticTableContainerKind.ObjectCreation, table.ContainerKind);
        Assert.Single(table.Rows);
    }

    [Fact]
    public void GetStaticTables_InstanceArrayField_NotReported()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public readonly int[] Numbers = new int[] { 1, 2 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        Assert.NotNull(report);
        Assert.Empty(report!.Tables);
    }

    [Fact]
    public void GetStaticTables_MemberNameFilter_LimitsToMatchingMember()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] A = new int[] { 1 };
    public static readonly int[] B = new int[] { 2, 3 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", new StaticTablesOptions { MemberName = "B" });

        var table = Assert.Single(report!.Tables);
        Assert.Equal("B", table.MemberName);
        Assert.Equal(2, table.Rows.Length);
    }

    [Fact]
    public void GetStaticTables_MultipleTables_OrderPreserved()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] First = new int[] { 1 };
    public static readonly int[] Second = new int[] { 2 };
    public static readonly int[] Third = new int[] { 3 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        Assert.Equal(3, report!.Tables.Length);
        Assert.Equal("First", report.Tables[0].MemberName);
        Assert.Equal("Second", report.Tables[1].MemberName);
        Assert.Equal("Third", report.Tables[2].MemberName);
    }

    [Fact]
    public void GetStaticTables_StaticPropertyInitializer_Reported()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static int[] Numbers { get; } = new int[] { 1, 2 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        var table = Assert.Single(report!.Tables);
        Assert.Equal("Numbers", table.MemberName);
        Assert.StartsWith("property:", table.MemberId);
        Assert.Equal(2, table.Rows.Length);
    }

    [Fact]
    public void GetStaticTables_LiteralIntArray_RowsCarryNumberValues()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] Numbers = new int[] { 7, 11, 13 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var table = Assert.Single(report!.Tables);

        Assert.All(table.Rows, r =>
        {
            Assert.Null(r.ConstructorId);
            Assert.Empty(r.Cells);
            Assert.NotNull(r.Value);
            Assert.Equal(StaticTableValueKind.Number, r.Value!.Kind);
        });
        Assert.Equal(7d, table.Rows[0].Value!.NumberValue);
        Assert.Equal(11d, table.Rows[1].Value!.NumberValue);
        Assert.Equal(13d, table.Rows[2].Value!.NumberValue);
    }

    [Fact]
    public void GetStaticTables_LiteralStringArray_RowsCarryStringValues()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly string[] Tags = new string[] { ""alpha"", ""beta"" };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var table = Assert.Single(report!.Tables);

        Assert.Equal(StaticTableValueKind.String, table.Rows[0].Value!.Kind);
        Assert.Equal("alpha", table.Rows[0].Value!.StringValue);
        Assert.Equal("beta", table.Rows[1].Value!.StringValue);
    }

    [Fact]
    public void GetStaticTables_LiteralBoolArray_RowsCarryBoolValues()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly bool[] Flags = new bool[] { true, false, true };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var table = Assert.Single(report!.Tables);

        Assert.Equal(StaticTableValueKind.Bool, table.Rows[0].Value!.Kind);
        Assert.True(table.Rows[0].Value!.BoolValue);
        Assert.False(table.Rows[1].Value!.BoolValue);
        Assert.True(table.Rows[2].Value!.BoolValue);
    }

    [Fact]
    public void GetStaticTables_LiteralObjectArrayWithNull_RowValueClassifiedAsNull()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly object[] Mixed = new object[] { null, 1 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var table = Assert.Single(report!.Tables);

        Assert.Equal(StaticTableValueKind.Null, table.Rows[0].Value!.Kind);
        Assert.Equal(StaticTableValueKind.Number, table.Rows[1].Value!.Kind);
    }

    [Fact]
    public void GetStaticTables_LiteralValuesCarryRawText()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static readonly int[] Numbers = new int[] { 42 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var v = report!.Tables[0].Rows[0].Value!;

        Assert.Equal("42", v.RawText);
        Assert.True(v.Line > 0);
        Assert.True(v.Column > 0);
    }

    [Fact]
    public void GetStaticTables_ExpressionBodiedStaticProperty_Reported()
    {
        const string source = @"
namespace Acme {
  public class Foo {
    public static int[] Numbers => new int[] { 1, 2, 3 };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);

        var table = Assert.Single(report!.Tables);
        Assert.Equal("Numbers", table.MemberName);
        Assert.Equal(3, table.Rows.Length);
    }
}
