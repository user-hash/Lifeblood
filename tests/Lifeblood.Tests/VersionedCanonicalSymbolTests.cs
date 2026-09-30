using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Lifeblood.Tests;

public class VersionedCanonicalSymbolTests
{
    private const CanonicalSymbolIdVersion V2 = CanonicalSymbolIdVersion.AssemblyQualifiedV2;

    [Theory]
    [InlineData("ref")]
    [InlineData("out")]
    [InlineData("in")]
    [InlineData("ref readonly")]
    public void Version2_PassingModeIsPartOfIdentity(string mode)
    {
        var body = mode == "out" ? "x = 0;" : "";
        var compilation = Compile("Modes", $"public class C {{ public void M(int x) {{}} public void M({mode} int x) {{ {body} }} }}");
        var methods = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers("M");
        Assert.Equal(2, methods.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        var consumer = Compile("Consumer", "", Emit(compilation));
        foreach (var method in methods)
        {
            AssertRoundTrip(compilation, method);
            var id = CanonicalSymbolFormat.BuildSymbolId(method, V2);
            var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.True(resolved != null, id);
        }
    }

    [Fact]
    public void Version2_EnclosingTypeArityDistinguishesEveryMember()
    {
        var compilation = Compile("Owners", """
            public class Outer<T> { public class Inner { public void M() {} public int X; } }
            public class Outer<T,U> { public class Inner { public void M() {} public int X; } }
            """);
        var symbols = compilation.GlobalNamespace.GetTypeMembers("Outer")
            .SelectMany(o => o.GetTypeMembers("Inner"))
            .SelectMany(i => new ISymbol[] { i, i.GetMembers("M").Single(), i.GetMembers("X").Single() }).ToArray();
        Assert.Equal(3, symbols.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct().Count());
        Assert.Equal(6, symbols.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var symbol in symbols) AssertRoundTrip(compilation, symbol);
    }

    [Fact]
    public void UnknownVersionCannotFallBackToLegacyIdentity()
    {
        var compilation = Compile("Version", "public class C { public int X; public int Value {get;set;} public event System.Action? Changed; public void M() {} }");
        var type = compilation.GlobalNamespace.GetTypeMembers("C").Single();
        foreach (var symbol in type.GetMembers().Append(type).Append(compilation.GlobalNamespace))
            Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalSymbolFormat.BuildSymbolId(symbol, (CanonicalSymbolIdVersion)3));
    }

    [Theory]
    [InlineData("public class C { public void M(int x) {} public void M(ref int x) {} }", "M")]
    [InlineData("public class C { public void M<T>() {} public void M<T,U>() {} }", "M")]
    [InlineData("public class Box {} public class Box<T> {}", "Box")]
    [InlineData("public class C { public static implicit operator int(C x) => 0; public static implicit operator string(C x) => \"\"; }", "op_Implicit")]
    public void Version2_DistinguishesLegalDeclarations_WhileLegacyBytesStayUnchanged(string source, string name)
    {
        var compilation = Compile("Declarations", source);
        var declarations = name == "Box"
            ? compilation.GlobalNamespace.GetTypeMembers(name).Cast<ISymbol>().ToArray()
            : compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers(name).ToArray();
        Assert.Equal(2, declarations.Length);
        Assert.Single(declarations.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct());
        Assert.Equal(2, declarations.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var declaration in declarations) AssertRoundTrip(compilation, declaration);
    }

    [Fact]
    public void Version2_RoundTripsNestedGenericMembers_AndSourceMetadataCopies()
    {
        var source = Compile("Library", """
            using System;
            using System.Collections.Generic;
            namespace App;
            public class Outer<T> {
                public class Inner<U> {
                    public Inner() {}
                    static Inner() {}
                    public T Field = default!;
                    public event Action? Changed;
                    public T this[int key] { get => Field; set => Field = value; }
                    public void M<V>(ref T x, out U y, in V z, List<T> list, int? first, string? second) { y = default!; Changed?.Invoke(); }
                    public void N(int? x) {}
                    public void N(long? x) {}
                    public void Dynamic(dynamic x) {}
                    public void Tuple((int A, string B) value) {}
                }
            }
            """);
        var metadata = Emit(source);
        var consumer = Compile("Consumer", "", metadata);
        var outer = source.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "App").GetTypeMembers("Outer").Single();
        var inner = outer.GetTypeMembers("Inner").Single();
        var symbols = new ISymbol[] { outer, inner }.Concat(inner.GetMembers()
            .Where(s => s is IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol)).ToArray();
        foreach (var symbol in symbols)
        {
            var normalized = Normalize(symbol);
            AssertRoundTrip(source, normalized);
            var id = CanonicalSymbolFormat.BuildSymbolId(normalized, V2);
            var fromMetadata = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.True(fromMetadata != null, id);
            Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(fromMetadata!, V2));
        }
        Assert.Equal(2, inner.GetMembers("N").Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        var constructed = inner.Construct(source.GetSpecialType(SpecialType.System_String));
        Assert.Equal(CanonicalSymbolFormat.BuildTypeId(inner, V2), CanonicalSymbolFormat.BuildTypeId(constructed, V2));
        Assert.Equal(CanonicalSymbolFormat.BuildFieldId((IFieldSymbol)inner.GetMembers("Field").Single(), V2),
            CanonicalSymbolFormat.BuildFieldId((IFieldSymbol)constructed.GetMembers("Field").Single(), V2));
    }

    [Fact]
    public void Version2_NormalizesPartialConstructedAndReducedMethods()
    {
        var compilation = Compile("Normalize", """
            public partial class C {
                partial void Partial(int x);
                partial void Partial(int x) {}
                public void Generic<T>(T x) {}
            }
            public static class Extensions {
                public static void Extend<T>(this C value, T x) {}
                public static void Call(C value) { value.Extend(1); }
            }
            """);
        var type = compilation.GlobalNamespace.GetTypeMembers("C").Single();
        var partial = (IMethodSymbol)type.GetMembers("Partial").Single();
        Assert.NotNull(partial.PartialImplementationPart);
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(partial, V2), CanonicalSymbolFormat.BuildMethodId(partial.PartialImplementationPart!, V2));
        var generic = (IMethodSymbol)type.GetMembers("Generic").Single();
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(generic, V2),
            CanonicalSymbolFormat.BuildMethodId(generic.Construct(compilation.GetSpecialType(SpecialType.System_Int32)), V2));
        var tree = compilation.SyntaxTrees.Single();
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var reduced = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol!;
        Assert.NotNull(reduced.ReducedFrom);
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(reduced.ReducedFrom!, V2), CanonicalSymbolFormat.BuildMethodId(reduced, V2));
        AssertRoundTrip(compilation, generic);
        AssertRoundTrip(compilation, partial);
    }

    [Fact]
    public void Version2_DistinguishesAssemblyOwnersAndExternAliasParameterTypes()
    {
        var first = Compile("First", "namespace Shared; public class Item {} public class C { public void M() {} }");
        var second = Compile("Second", "namespace Shared; public class Item {} public class C { public void M() {} }");
        var consumer = Compile("Consumer", """
            extern alias A;
            extern alias B;
            public class Overloads {
                public void M(A::Shared.Item item) {}
                public void M(B::Shared.Item item) {}
            }
            """, Emit(first).WithAliases(new[] { "A" }), Emit(second).WithAliases(new[] { "B" }));
        var overloads = consumer.GlobalNamespace.GetTypeMembers("Overloads").Single().GetMembers("M");
        Assert.Single(overloads.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct());
        Assert.Equal(2, overloads.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var method in overloads) AssertRoundTrip(consumer, method);
        var ownerIds = new HashSet<string>();
        foreach (var compilation in new[] { first, second })
        {
            var owner = compilation.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "Shared").GetTypeMembers("C").Single();
            foreach (var symbol in new ISymbol[] { owner, owner.GetMembers("M").Single() })
            {
                var id = CanonicalSymbolFormat.BuildSymbolId(symbol, V2);
                Assert.True(ownerIds.Add(id));
                var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
                Assert.NotNull(resolved);
                Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(resolved!, V2));
            }
        }
    }

    [Fact]
    public void Version2_RoundTripsPointerFunctionPointerAndExplicitInterfaceMembers()
    {
        var compilation = Compile("Unsafe", """
            public interface I { void M(); int Value { get; } }
            public unsafe class C : I {
                void I.M() {}
                int I.Value => 1;
                public void P(int* x) {}
                public void P(long* x) {}
                public void F(delegate*<int, void> x) {}
                public void F(delegate* unmanaged[Cdecl]<int, void> x) {}
            }
            """);
        var members = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers()
            .Where(s => s is IMethodSymbol or IPropertySymbol).Select(Normalize).ToArray();
        foreach (var member in members) AssertRoundTrip(compilation, member);
        var ids = members.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().ToArray();
        Assert.Equal(members.Distinct(SymbolEqualityComparer.Default).Count(), ids.Length);
        var consumer = Compile("Consumer", "", Emit(compilation));
        foreach (var id in ids)
        {
            var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.NotNull(resolved);
            Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(resolved!, V2));
        }
    }

    [Fact]
    public void Version2_RejectsMalformedOrInexactIds()
    {
        var compilation = Compile("Exact", "public class C { public void M() {} }");
        var method = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers("M").Single();
        var id = CanonicalSymbolFormat.BuildSymbolId(method, V2);
        foreach (var invalid in new[] { "method:v2|", id + "|extra", id + "wrong", id.Replace("method:", "field:") })
            Assert.Null(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(invalid)));
    }

    private static ISymbol Normalize(ISymbol symbol)
        => symbol is IMethodSymbol { AssociatedSymbol: { } owner } ? owner : symbol;

    private static void AssertRoundTrip(CSharpCompilation compilation, ISymbol symbol)
    {
        var id = CanonicalSymbolFormat.BuildSymbolId(symbol, V2);
        var resolved = RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(id));
        Assert.True(resolved != null, id);
        Assert.True(SymbolEqualityComparer.Default.Equals(symbol, resolved), id);
    }

    private static CSharpCompilation Compile(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source, path: name + ".cs") },
            BclReferenceLoader.References.Value.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                metadataImportOptions: MetadataImportOptions.All));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    private static PortableExecutableReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
