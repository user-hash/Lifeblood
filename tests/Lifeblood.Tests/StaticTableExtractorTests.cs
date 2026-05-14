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
    public void GetStaticTables_ObjectCreationRow_CellsBoundByPositionAndName()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int id, string name) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(1, ""alpha""), new Row(2, ""beta"") };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var table = Assert.Single(report!.Tables);
        Assert.Equal(2, table.Rows.Length);

        var row0 = table.Rows[0];
        Assert.NotNull(row0.ConstructorId);
        Assert.Null(row0.Value);
        Assert.Equal(2, row0.Cells.Length);

        Assert.Equal("id", row0.Cells[0].ParameterName);
        Assert.Equal(0, row0.Cells[0].Position);
        Assert.Equal(StaticTableArgumentKind.Explicit, row0.Cells[0].ArgumentKind);
        Assert.Equal(StaticTableValueKind.Number, row0.Cells[0].Value.Kind);
        Assert.Equal(1d, row0.Cells[0].Value.NumberValue);

        Assert.Equal("name", row0.Cells[1].ParameterName);
        Assert.Equal(1, row0.Cells[1].Position);
        Assert.Equal(StaticTableValueKind.String, row0.Cells[1].Value.Kind);
        Assert.Equal("alpha", row0.Cells[1].Value.StringValue);
    }

    [Fact]
    public void GetStaticTables_DefaultArgument_ReportedAsDefaultValueKind()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int id, int multiplier = 7) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(1) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var row = report!.Tables[0].Rows[0];

        Assert.Equal(2, row.Cells.Length);
        Assert.Equal(StaticTableArgumentKind.Explicit, row.Cells[0].ArgumentKind);
        Assert.Equal(StaticTableArgumentKind.DefaultValue, row.Cells[1].ArgumentKind);
        Assert.Equal("multiplier", row.Cells[1].ParameterName);
        Assert.Equal(StaticTableValueKind.Number, row.Cells[1].Value.Kind);
        Assert.Equal(7d, row.Cells[1].Value.NumberValue);
    }

    [Fact]
    public void GetStaticTables_NamedArgument_OutOfOrderStillBindsToParameterPosition()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int id, string name) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(name: ""alpha"", id: 5) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cells = report!.Tables[0].Rows[0].Cells;

        var idCell = Assert.Single(cells, c => c.ParameterName == "id");
        Assert.Equal(0, idCell.Position);
        Assert.Equal(5d, idCell.Value.NumberValue);

        var nameCell = Assert.Single(cells, c => c.ParameterName == "name");
        Assert.Equal(1, nameCell.Position);
        Assert.Equal("alpha", nameCell.Value.StringValue);
    }

    [Fact]
    public void GetStaticTables_SingleObjectCreationTable_RowCellsBound()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int id) { } }
  public class Foo {
    public static readonly Row Default = new Row(42);
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var row = report!.Tables[0].Rows[0];

        Assert.Equal(StaticTableContainerKind.ObjectCreation, report.Tables[0].ContainerKind);
        var cell = Assert.Single(row.Cells);
        Assert.Equal("id", cell.ParameterName);
        Assert.Equal(42d, cell.Value.NumberValue);
    }

    [Fact]
    public void GetStaticTables_SingleEnumMember_ClassifiedAsEnumMember()
    {
        const string source = @"
namespace Acme {
  public enum Mode { Alpha, Beta, Gamma }
  public class Row { public Row(Mode m) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(Mode.Beta) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.EnumMember, cell.Value.Kind);
        Assert.NotNull(cell.Value.EnumMemberId);
        Assert.Contains(".Beta", cell.Value.EnumMemberId);
    }

    [Fact]
    public void GetStaticTables_ComposedEnumFlags_ClassifiedAsEnumFlags()
    {
        const string source = @"
namespace Acme {
  [System.Flags] public enum Bits { None=0, A=1, B=2, C=4 }
  public class Row { public Row(Bits b) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(Bits.A | Bits.B) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.EnumFlags, cell.Value.Kind);
        Assert.NotNull(cell.Value.EnumFlagMemberIds);
        Assert.Equal(2, cell.Value.EnumFlagMemberIds!.Length);
        Assert.Contains(cell.Value.EnumFlagMemberIds, id => id.EndsWith(".A"));
        Assert.Contains(cell.Value.EnumFlagMemberIds, id => id.EndsWith(".B"));
    }

    [Fact]
    public void GetStaticTables_NestedEnumFlags_FlattenedInAuthoringOrder()
    {
        const string source = @"
namespace Acme {
  [System.Flags] public enum Bits { None=0, A=1, B=2, C=4, D=8 }
  public class Row { public Row(Bits b) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(Bits.A | Bits.B | Bits.D) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.EnumFlags, cell.Value.Kind);
        Assert.Equal(3, cell.Value.EnumFlagMemberIds!.Length);
        Assert.EndsWith(".A", cell.Value.EnumFlagMemberIds[0]);
        Assert.EndsWith(".B", cell.Value.EnumFlagMemberIds[1]);
        Assert.EndsWith(".D", cell.Value.EnumFlagMemberIds[2]);
    }

    [Fact]
    public void GetStaticTables_SingleFlagValueWithoutOr_ClassifiedAsEnumMember()
    {
        const string source = @"
namespace Acme {
  [System.Flags] public enum Bits { None=0, A=1, B=2 }
  public class Row { public Row(Bits b) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(Bits.A) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        // A bare flag value not composed via | is still a single enum
        // member reference — caller can compose downstream.
        Assert.Equal(StaticTableValueKind.EnumMember, cell.Value.Kind);
        Assert.EndsWith(".A", cell.Value.EnumMemberId!);
    }

    [Fact]
    public void GetStaticTables_MethodGroupCell_ClassifiedAsMethodGroup()
    {
        const string source = @"
using System;
namespace Acme {
  public class Row { public Row(Func<int> producer) { } }
  public class Foo {
    static int Source() => 7;
    public static readonly Row[] All = new Row[] { new Row(Source) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.MethodGroup, cell.Value.Kind);
        Assert.NotNull(cell.Value.MethodGroupId);
        Assert.Contains("Source", cell.Value.MethodGroupId);
        Assert.StartsWith("method:", cell.Value.MethodGroupId);
    }

    [Fact]
    public void GetStaticTables_InlineLambdaCell_FallsBackToComputed()
    {
        const string source = @"
using System;
namespace Acme {
  public class Row { public Row(Func<int> producer) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(() => 11) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        // Inline lambdas are not method groups — extractor does not
        // peek inside the body. Computed is the eternal fallback.
        Assert.Equal(StaticTableValueKind.Computed, cell.Value.Kind);
        Assert.Contains("=>", cell.Value.RawText);
    }

    [Fact]
    public void GetStaticTables_StaticFieldReferenceCell_ClassifiedAsFieldReference()
    {
        const string source = @"
namespace Acme {
  public static class Shared { public static readonly int Common = 99; }
  public class Row { public Row(int id) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(Shared.Common) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.FieldReference, cell.Value.Kind);
        Assert.NotNull(cell.Value.FieldReferenceId);
        Assert.Contains("Common", cell.Value.FieldReferenceId);
    }

    [Fact]
    public void GetStaticTables_NestedArrayCell_ClassifiedAsArrayWithElements()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int[] tags) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row(new int[] { 1, 2, 3 }) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.Array, cell.Value.Kind);
        Assert.NotNull(cell.Value.ArrayElements);
        Assert.Equal(3, cell.Value.ArrayElements!.Length);
        Assert.Equal(StaticTableValueKind.Number, cell.Value.ArrayElements[0].Kind);
        Assert.Equal(1d, cell.Value.ArrayElements[0].NumberValue);
        Assert.Equal(2d, cell.Value.ArrayElements[1].NumberValue);
    }

    [Fact]
    public void GetStaticTables_NestedCollectionExpressionCell_ClassifiedAsArray()
    {
        const string source = @"
namespace Acme {
  public class Row { public Row(int[] tags) { } }
  public class Foo {
    public static readonly Row[] All = new Row[] { new Row([ 7, 11 ]) };
  }
}";
        using var host = HostWith(source);

        var report = host.GetStaticTables("type:Acme.Foo", Default);
        var cell = report!.Tables[0].Rows[0].Cells[0];

        Assert.Equal(StaticTableValueKind.Array, cell.Value.Kind);
        Assert.Equal(2, cell.Value.ArrayElements!.Length);
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
