using System.Reflection;
using System.Runtime.InteropServices;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-STRUCT-LAYOUT-001. The exact lane is pinned against real runtime layout:
/// the same fixture source is emitted for <see cref="Marshal.SizeOf(Type)"/> /
/// <see cref="Marshal.OffsetOf(Type, string)"/> ground truth and parsed by the
/// Roslyn extractor for offline computation.
/// </summary>
public class StructLayoutExtractorTests
{
    private const string FixtureSource = @"
using System.Runtime.InteropServices;

namespace Acme;

public enum Tiny : short { A = 1, B = 2 }

public struct Inner
{
    public short A;
    public byte B;
}

public struct Outer
{
    public byte A;
    public Inner B;
    public int C;
    public Tiny D;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Packed
{
    public byte A;
    public int B;
    public short C;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct ExplicitUnion
{
    [FieldOffset(0)] public int I;
    [FieldOffset(0)] public float F;
    [FieldOffset(8)] public short S;
}

public unsafe struct FixedBufferHolder
{
    public fixed int Values[3];
    public byte Tail;
}

[StructLayout(LayoutKind.Auto)]
public struct AutoLayout
{
    public int A;
    public byte B;
}

public struct ReferenceBearing
{
    public int A;
    public string Text;
}
";

    private static (RoslynCompilationHost Host, Assembly Asm) BuildBoth()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
        };
        var tree = CSharpSyntaxTree.ParseText(FixtureSource, path: "Layout.cs",
            options: new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create("LayoutAsm", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, "fixture must compile: " + string.Join("; ", emit.Diagnostics));
        var asm = Assembly.Load(ms.ToArray());

        var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["LayoutAsm"] = compilation,
        });
        return (host, asm);
    }

    [Theory]
    [InlineData("Acme.Inner", "type:Acme.Inner")]
    [InlineData("Acme.Outer", "type:Acme.Outer")]
    [InlineData("Acme.Packed", "type:Acme.Packed")]
    [InlineData("Acme.ExplicitUnion", "type:Acme.ExplicitUnion")]
    [InlineData("Acme.FixedBufferHolder", "type:Acme.FixedBufferHolder")]
    public void ExactLayouts_MatchRealMarshalSizeAndOffsets(string reflectionName, string typeId)
    {
        var (host, asm) = BuildBoth();
        var reflected = asm.GetType(reflectionName)!;
        var report = host.GetStructLayout(typeId);

        Assert.NotNull(report);
        Assert.Equal(StructLayoutConfidence.Exact, report!.Confidence);
        Assert.True(report.IsBlittable);
        Assert.Equal(Marshal.SizeOf(reflected), report.Size);

        foreach (var field in report.Fields)
        {
            Assert.NotNull(field.Offset);
            Assert.Equal(Marshal.OffsetOf(reflected, field.Name).ToInt32(), field.Offset!.Value);
            Assert.True(field.Size > 0);
            Assert.True(field.Alignment > 0);
        }
    }

    [Fact]
    public void PackedLayout_HonorsPackOne()
    {
        var (host, _) = BuildBoth();
        var report = host.GetStructLayout("type:Acme.Packed")!;

        Assert.Equal(1, report.Pack);
        Assert.Equal(7, report.Size);
        Assert.Equal(0, Field(report, "A").Offset);
        Assert.Equal(1, Field(report, "B").Offset);
        Assert.Equal(5, Field(report, "C").Offset);
    }

    [Fact]
    public void ExplicitLayout_HonorsFieldOffsetAndDeclaredSize()
    {
        var (host, _) = BuildBoth();
        var report = host.GetStructLayout("type:Acme.ExplicitUnion")!;

        Assert.Equal(StructLayoutKind.Explicit, report.LayoutKind);
        Assert.Equal(16, report.DeclaredSize);
        Assert.Equal(16, report.Size);
        Assert.Equal(0, Field(report, "I").Offset);
        Assert.Equal(0, Field(report, "F").Offset);
        Assert.Equal(8, Field(report, "S").Offset);
    }

    [Fact]
    public void FixedBuffer_FieldCarriesExpandedSizeAndLength()
    {
        var (host, _) = BuildBoth();
        var report = host.GetStructLayout("type:Acme.FixedBufferHolder")!;
        var values = Field(report, "Values");

        Assert.Equal(3, values.FixedBufferLength);
        Assert.Equal(12, values.Size);
        Assert.Equal(4, values.Alignment);
        Assert.Equal(12, Field(report, "Tail").Offset);
    }

    [Fact]
    public void AdvisoryLayouts_SurfaceLimitations()
    {
        var (host, _) = BuildBoth();

        var auto = host.GetStructLayout("type:Acme.AutoLayout")!;
        Assert.Equal(StructLayoutKind.Auto, auto.LayoutKind);
        Assert.Equal(StructLayoutConfidence.Advisory, auto.Confidence);
        Assert.Contains(auto.Limitations, l => l.Contains("Auto", StringComparison.Ordinal));
        Assert.All(auto.Fields, f => Assert.Null(f.Offset));

        var refs = host.GetStructLayout("type:Acme.ReferenceBearing")!;
        Assert.Equal(StructLayoutConfidence.Advisory, refs.Confidence);
        Assert.False(refs.IsBlittable);
        Assert.Contains(refs.Limitations, l => l.Contains("reference-bearing", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOrNonStruct_ReturnsNull()
    {
        var (host, _) = BuildBoth();

        Assert.Null(host.GetStructLayout("type:Acme.DoesNotExist"));
    }

    private static StructLayoutField Field(StructLayoutReport report, string name)
        => report.Fields.Single(f => f.Name == name);
}
