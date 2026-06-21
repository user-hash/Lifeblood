using System.Reflection;
using System.Runtime.CompilerServices;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-MEMBER-COUNT-001. <c>reflectionDeclared</c> must equal a REAL
/// <c>System.Reflection</c> DeclaredOnly count. The harness compiles one fixture
/// source string TWICE: it emits + loads the assembly for ground-truth reflection,
/// and feeds the same compilation to the tool — so parity is guaranteed by
/// construction over a fixture that exercises auto-properties, full properties,
/// field-like + custom events, operators, static/instance/private members, the
/// implicit default ctor, and a nested type. Neutral fixtures (<c>Acme</c>).
/// </summary>
public class MemberCountExtractorTests
{
    private const string FixtureSource = @"
namespace Acme;
public class Fixture
{
    public const int K = 3;
    private int _field;
    public static readonly string S = ""x"";
    public int Auto { get; set; }
    private bool _backed;
    public bool Full { get => _backed; set => _backed = value; }
    public event System.Action FieldLike;
    private System.Action _h;
    public event System.Action Custom { add { _h += value; } remove { _h -= value; } }
    public Fixture(int x) { _field = x; }
    static Fixture() { }
    public void M() { FieldLike?.Invoke(); }
    private static int Helper(int a) => a + 1;
    public static Fixture operator +(Fixture a, Fixture b) => a;
    public class Nested { public int N; }
}
public class ImplicitCtor { public int Value; public int Read() => Value; }";

    private static (RoslynCompilationHost Host, Assembly Asm) BuildBoth()
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "netstandard.dll")),
        };
        var tree = CSharpSyntaxTree.ParseText(FixtureSource, path: "Fixture.cs");
        var compilation = CSharpCompilation.Create("FixtureAsm", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, "fixture must compile: " + string.Join("; ", emit.Diagnostics));
        var asm = Assembly.Load(ms.ToArray());

        var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["FixtureAsm"] = compilation,
        });
        return (host, asm);
    }

    private static int ReflectionDeclaredCount(System.Type t)
    {
        const BindingFlags F = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static;
        static bool NotCg(MemberInfo m) => !m.IsDefined(typeof(CompilerGeneratedAttribute), false);
        return t.GetMethods(F).Count(NotCg)
             + t.GetConstructors(F).Count(NotCg)
             + t.GetFields(F).Count(NotCg)
             + t.GetProperties(F).Count(NotCg)
             + t.GetEvents(F).Count(NotCg);
    }

    [Theory]
    [InlineData("Acme.Fixture", "type:Acme.Fixture")]
    [InlineData("Acme.ImplicitCtor", "type:Acme.ImplicitCtor")]
    public void ReflectionDeclared_MatchesRealReflection_BitExact(string typeName, string typeId)
    {
        var (host, asm) = BuildBoth();
        var reflected = asm.GetType(typeName)!;
        var expected = ReflectionDeclaredCount(reflected);

        var report = host.GetMemberCount(typeId, MemberCountSemantics.ReflectionDeclared);
        Assert.NotNull(report);
        Assert.Equal(MemberCountSemantics.ReflectionDeclared, report!.Semantics);
        Assert.Equal(expected, report.Count);
    }

    [Fact]
    public void ReflectionDeclared_ExcludesNestedTypes_AndCountsImplicitCtor()
    {
        var (host, _) = BuildBoth();
        var implicitCtor = host.GetMemberCount("type:Acme.ImplicitCtor", MemberCountSemantics.ReflectionDeclared)!;
        // Value field + Read method + implicit default ctor = 3; no nested types.
        Assert.Equal(1, implicitCtor.Breakdown.Constructors);
        Assert.Equal(0, implicitCtor.Breakdown.NestedTypes);

        var fixture = host.GetMemberCount("type:Acme.Fixture", MemberCountSemantics.ReflectionDeclared)!;
        Assert.Equal(1, fixture.Breakdown.NestedTypes);                  // Nested counted but excluded from Count
        Assert.True(fixture.Breakdown.Excluded >= 2, "auto-prop + field-like-event backing fields are compiler-generated");
    }

    [Fact]
    public void SourceSymbols_IncludesNested_ExcludesAccessorsAndImplicitCtor()
    {
        var (host, _) = BuildBoth();
        var src = host.GetMemberCount("type:Acme.Fixture", MemberCountSemantics.SourceSymbols)!;
        Assert.Equal(MemberCountSemantics.SourceSymbols, src.Semantics);
        Assert.Equal(1, src.Breakdown.NestedTypes);

        // sourceSymbols counts the declared property/event ONCE (no get_/set_/add_/remove_),
        // so it is strictly smaller than reflectionDeclared on a type with accessors.
        var refl = host.GetMemberCount("type:Acme.Fixture", MemberCountSemantics.ReflectionDeclared)!;
        Assert.True(src.Count < refl.Count, "source symbols drop synthesized accessors that reflection counts");

        // The implicit default ctor has no source declaration → not counted here.
        var implicitCtor = host.GetMemberCount("type:Acme.ImplicitCtor", MemberCountSemantics.SourceSymbols)!;
        Assert.Equal(0, implicitCtor.Breakdown.Constructors);
    }

    [Fact]
    public void GetMemberCount_UnknownType_ReturnsNull()
    {
        var (host, _) = BuildBoth();
        Assert.Null(host.GetMemberCount("type:Acme.DoesNotExist", MemberCountSemantics.ReflectionDeclared));
    }
}
