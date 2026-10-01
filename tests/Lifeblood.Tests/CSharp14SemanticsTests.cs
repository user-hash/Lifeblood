using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using SymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

public class CSharp14SemanticsTests
{
    [Fact]
    public void ModernMembers_CompileAndRetainSourceDeclarations()
    {
        WithWorkspace("""
            using System;
            namespace Features;
            public partial class Sample
            {
                public int Value { get => field; set => field = Math.Max(0, value); }
                public partial Sample(int value);
                public partial Sample(int value) { Value = value; }
                public partial event Action Changed;
                public partial event Action Changed { add { } remove { } }
                public void Set(Sample? target) { target?.Value = 2; }
                public static int Sum(ReadOnlySpan<int> values) => values.Length;
                public static int Run() => Sum(new[] { 1, 2 });
            }
            """, (graph, analyzer) =>
        {
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Value" && symbol.Kind == SymbolKind.Property);
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Changed");
            Assert.Contains(graph.Edges, edge => edge.Kind == EdgeKind.Calls && edge.TargetId.Contains("Sum"));
        });
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void ExtensionMembers_DeclarationAndCallUseOneIdentity(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public static class Extensions
            {
                extension(string text)
                {
                    public int Size => text.Length;
                    public string Echo() => text;
                }
            }
            public class Runner
            {
                public int Run() => "sample".Echo().Size;
            }
            """, (graph, analyzer) =>
        {
            var method = Assert.Single(graph.Symbols, symbol => symbol.Name == "Echo");
            var property = Assert.Single(graph.Symbols, symbol => symbol.Name == "Size");
            Assert.Contains(graph.Edges, edge => edge.TargetId == method.Id && edge.Kind == EdgeKind.Calls);
            Assert.Contains(graph.Edges, edge => edge.TargetId == property.Id);
            var compilation = analyzer.Compilations!["Test"];
            Assert.True(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(method.Id)) is not null,
                $"Extension declaration did not resolve: {method.Id}");
            using var host = new RoslynCompilationHost(analyzer.Compilations!, idVersion: version);
            Assert.NotEmpty(host.FindReferences(method.Id));
            Assert.NotEmpty(host.FindReferences(property.Id));
            if (version == CanonicalSymbolIdVersion.AssemblyQualifiedV2)
            {
                using var image = new MemoryStream();
                var emit = compilation.Emit(image);
                Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
                var consumer = CSharpCompilation.Create("Consumer", references:
                    BclReferenceLoader.References.Value.Append(MetadataReference.CreateFromImage(image.ToArray())));
                Assert.NotNull(RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(method.Id)));
                Assert.NotNull(RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(property.Id)));
            }
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void InstanceOperators_KeepCompilerFlagsAndCallsInBothDirections(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public class Counter
            {
                public int Value;
                public static int Adjust(int value) => value + 1;
                public void operator +=(int value) { Value = Adjust(value); }
                public void operator ++() { int Next() => Adjust(Value); Value = Next(); }
                public static void Run(Counter counter) { counter += 2; counter++; counter += 3; }
                ~Counter() { Adjust(Value); }
            }
            """, (graph, analyzer) =>
        {
            var run = Assert.Single(graph.Symbols, symbol => symbol.Name == "Run");
            var adjust = Assert.Single(graph.Symbols, symbol => symbol.Name == "Adjust");
            var operators = graph.Symbols.Where(symbol => symbol.Properties.ContainsKey("isOperator")).ToArray();
            Assert.Equal(2, operators.Length);
            foreach (var op in operators)
            {
                Assert.False(op.IsStatic);
                var call = Assert.Single(graph.Edges, edge => edge.SourceId == run.Id && edge.TargetId == op.Id && edge.Kind == EdgeKind.Calls);
                Assert.NotNull(call.CallSite);
                Assert.Equal("Test.cs", call.CallSite.FilePath);
                Assert.Equal(run.Id, call.CallSite.ContainingSymbolId);
                Assert.Contains(graph.Edges, edge => edge.SourceId == op.Id && edge.TargetId == adjust.Id && edge.Kind == EdgeKind.Calls);
            }
            var destructor = Assert.Single(graph.Symbols, symbol => symbol.Name == "Finalize");
            Assert.Contains(graph.Edges, edge => edge.SourceId == destructor.Id && edge.TargetId == adjust.Id && edge.Kind == EdgeKind.Calls);
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void BoundOperators_IncludeCheckedLiftedImplicitAndGenericCalls(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public struct Number
            {
                public int Value;
                public static int Read(Number value) => value.Value;
                public static Number operator +(Number a, Number b) => new Number { Value = Read(a) + Read(b) };
                public static Number operator checked +(Number a, Number b) => new Number { Value = checked(Read(a) + Read(b)) };
                public static Number operator -(Number a) => new Number { Value = -Read(a) };
                public static Number operator ++(Number a) => new Number { Value = Read(a) + 1 };
                public static implicit operator Number(int a) => new Number { Value = a };
                public static explicit operator int(Number a) => Read(a);
            }
            public struct Box<T>
            {
                public static Box<T> operator +(Box<T> a, Box<T> b) => a;
            }
            public class Runner
            {
                public Number Run(Number a, Number b) { a += b; a++; Number value = 3; int result = (int)value; return -checked(a + b); }
                public Number? Lifted(Number? a, Number? b) => a + b;
                public Box<int> Generic(Box<int> a, Box<int> b) => a + b;
                public int Builtin(int a, int b) => a + b;
            }
            """, (graph, analyzer) =>
        {
            var run = Assert.Single(graph.Symbols, symbol => symbol.Name == "Run");
            var operators = graph.Symbols.Where(symbol => symbol.Properties.ContainsKey("isOperator")).ToArray();
            Assert.Equal(7, operators.Length);
            foreach (var op in operators.Where(symbol => symbol.QualifiedName.StartsWith("Features.Number.")))
            {
                Assert.True(op.IsStatic);
                Assert.Contains(graph.Edges, edge => edge.SourceId == run.Id && edge.TargetId == op.Id && edge.Kind == EdgeKind.Calls);
            }
            var read = Assert.Single(graph.Symbols, symbol => symbol.Name == "Read");
            var conversion = Assert.Single(operators, symbol => symbol.Name == "op_Explicit");
            Assert.Contains(graph.Edges, edge => edge.SourceId == conversion.Id && edge.TargetId == read.Id && edge.Kind == EdgeKind.Calls);
            var plus = Assert.Single(operators, symbol => symbol.Name == "op_Addition" && symbol.QualifiedName.StartsWith("Features.Number."));
            var lifted = Assert.Single(graph.Symbols, symbol => symbol.Name == "Lifted");
            Assert.Contains(graph.Edges, edge => edge.SourceId == lifted.Id && edge.TargetId == plus.Id && edge.Kind == EdgeKind.Calls);
            var generic = Assert.Single(graph.Symbols, symbol => symbol.Name == "Generic");
            var genericPlus = Assert.Single(operators, symbol => symbol.QualifiedName.StartsWith("Features.Box"));
            Assert.Contains(graph.Edges, edge => edge.SourceId == generic.Id && edge.TargetId == genericPlus.Id && edge.Kind == EdgeKind.Calls);
            using var image = new MemoryStream();
            Assert.True(analyzer.Compilations!["Test"].Emit(image).Success);
            var consumerTree = CSharpSyntaxTree.ParseText("""
                namespace Consumer;
                public class Runner
                {
                    public Features.Box<int> Run(Features.Box<int> a, Features.Box<int> b) => a + b;
                }
                """);
            var consumer = CSharpCompilation.Create("Consumer", new[] { consumerTree },
                BclReferenceLoader.References.Value.Append(MetadataReference.CreateFromImage(image.ToArray())),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(consumer.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            var extractor = new RoslynEdgeExtractor(version) { KnownModuleAssemblies = new HashSet<string> { "Test" } };
            Assert.Contains(extractor.Extract(consumer.GetSemanticModel(consumerTree), consumerTree.GetRoot()),
                edge => edge.TargetId == genericPlus.Id && edge.Kind == EdgeKind.Calls);
            var builtin = Assert.Single(graph.Symbols, symbol => symbol.Name == "Builtin");
            Assert.DoesNotContain(graph.Edges, edge => edge.SourceId == builtin.Id && edge.Kind == EdgeKind.Calls);
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void ExtensionAndAbstractOperators_UseDeclaredIdentities(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public struct Point { public int X; }
            public static class Extensions
            {
                extension(Point)
                {
                    public static Point operator +(Point a, Point b) => new Point { X = a.X + b.X };
                }
            }
            public interface IAdd<T> where T : IAdd<T>
            {
                static abstract T operator +(T a, T b);
                static abstract implicit operator int(T a);
            }
            public class Runner
            {
                public Point Run(Point a, Point b) => a + b;
                public T Generic<T>(T a, T b) where T : IAdd<T> => a + b;
                public int Convert<T>(T a) where T : IAdd<T> => a;
            }
            """, (graph, analyzer) =>
        {
            var run = Assert.Single(graph.Symbols, symbol => symbol.Name == "Run");
            var generic = Assert.Single(graph.Symbols, symbol => symbol.Name == "Generic");
            var convert = Assert.Single(graph.Symbols, symbol => symbol.Name == "Convert");
            var operators = graph.Symbols.Where(symbol => symbol.Properties.ContainsKey("isOperator")).ToArray();
            Assert.Equal(3, operators.Length);
            var extension = Assert.Single(operators, symbol => symbol.QualifiedName.StartsWith("Features.Extensions"));
            Assert.Contains(graph.Edges, edge => edge.SourceId == run.Id && edge.TargetId == extension.Id && edge.Kind == EdgeKind.Calls);
            foreach (var op in operators.Where(symbol => symbol.QualifiedName.StartsWith("Features.IAdd")))
            {
                Assert.True(op.IsStatic);
                Assert.True(op.IsAbstract);
                var caller = op.Name == "op_Implicit" ? convert : generic;
                Assert.Contains(graph.Edges, edge => edge.SourceId == caller.Id && edge.TargetId == op.Id && edge.Kind == EdgeKind.Calls);
            }
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void CompoundConversionsAndInitializers_KeepImplicitCallOwnership(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            using System;
            namespace Features;
            public class Value
            {
                public static implicit operator int(Value value) => 1;
                public static implicit operator Value(int value) => new Value();
            }
            public class Runner
            {
                public static Value Field = (Value)2;
                public static int Property { get; } = new Value();
                public int Run(Value value) { value += 3; Func<int> read = () => value; return read(); }
            }
            """, (graph, analyzer) =>
        {
            var run = Assert.Single(graph.Symbols, symbol => symbol.Name == "Run");
            var cctor = Assert.Single(graph.Symbols, symbol => symbol.Name == ".cctor");
            var operators = graph.Symbols.Where(symbol => symbol.Properties.ContainsKey("isOperator")).ToArray();
            Assert.Equal(2, operators.Length);
            foreach (var op in operators)
            {
                var call = Assert.Single(graph.Edges, edge => edge.SourceId == run.Id && edge.TargetId == op.Id && edge.Kind == EdgeKind.Calls);
                Assert.NotNull(call.CallSite);
                Assert.Equal(12, call.CallSite.Line);
                Assert.Equal(run.Id, call.CallSite.ContainingSymbolId);
                Assert.Contains(graph.Edges, edge => edge.SourceId == cctor.Id && edge.TargetId == op.Id && edge.Kind == EdgeKind.Calls);
            }
        }, version);
    }

    [Fact]
    public void OperationRoots_PreserveIndependentAttributesAndParameterDefaults()
    {
        WithWorkspace("""
            using System;
            namespace Features;
            public class MarkAttribute : Attribute { public MarkAttribute(int value) { } }
            [Mark(2)]
            public class Runner
            {
                public static int Value = 1;
                [Mark(3)]
                public int Run(int value = 4)
                {
                    int Local(int other = 5) => other;
                    Func<int, int> lambda = (int other = 6) => other;
                    return Local() + lambda(value);
                }
            }
            """, (graph, analyzer) =>
        {
            var compilation = analyzer.Compilations!["Test"];
            var tree = Assert.Single(compilation.SyntaxTrees, syntax => syntax.FilePath.EndsWith("Test.cs"));
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var expected = root.DescendantNodesAndSelf().Select(node => model.GetOperation(node))
                .Where(operation => operation != null && operation.Parent == null)
                .Distinct(ReferenceEqualityComparer.Instance).ToArray();
            var actual = RoslynOperationFacts.FindOperationRoots(model, root);
            Assert.Equal(expected.Length, actual.Count);
            for (var index = 0; index < expected.Length; index++) Assert.Same(expected[index], actual[index]);
            Assert.Contains(actual, operation => operation is IFieldInitializerOperation);
            Assert.Contains(actual, operation => operation is IParameterInitializerOperation);
            Assert.Contains(actual, operation => operation is IAttributeOperation);
            Assert.Throws<OperationCanceledException>(() =>
                RoslynOperationFacts.FindOperationRoots(model, root, new CancellationToken(true)));
        });
    }

    private static void WithWorkspace(string source, Action<SemanticGraph, RoslynWorkspaceAnalyzer> verify,
        CanonicalSymbolIdVersion version = CanonicalSymbolIdVersion.LegacyV1)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-csharp14-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Test.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion><Nullable>enable</Nullable>
                </PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(root, "Test.cs"), source);
            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem(), idVersion: version);
            var graph = analyzer.AnalyzeWorkspace(root, new AnalysisConfig { RetainCompilations = true });
            Assert.Empty(analyzer.Compilations!["Test"].GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
            verify(graph, analyzer);
        }
        finally { Directory.Delete(root, true); }
    }
}
