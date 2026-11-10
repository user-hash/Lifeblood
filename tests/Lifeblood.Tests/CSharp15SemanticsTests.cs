using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using SymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

public sealed class CSharp15SemanticsTests
{
    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Unions_RetainTopLevelAndNestedTypesAndReferences(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public record Cat(string Name);
            public record Dog(string Name);
            public union Pet(Cat, Dog);
            public class Container { public union Nested(Cat, Dog); }
            public class Runner
            {
                public Pet Wrap(Cat cat) => cat;
                public Container.Nested Nest(Dog dog) => dog;
                public string Read(Pet pet) => pet switch { Cat cat => cat.Name, Dog dog => dog.Name };
            }
            """, (graph, analyzer) =>
        {
            var pet = Assert.Single(graph.Symbols, symbol => symbol.Name == "Pet" && symbol.Kind == SymbolKind.Type);
            var nested = Assert.Single(graph.Symbols, symbol => symbol.Name == "Nested" && symbol.Kind == SymbolKind.Type);
            var container = Assert.Single(graph.Symbols, symbol => symbol.Name == "Container");
            Assert.Equal(container.Id, nested.ParentId);
            using var host = new RoslynCompilationHost(analyzer.Compilations!, idVersion: version);
            Assert.NotEmpty(host.FindReferences(pet.Id));
            Assert.NotEmpty(host.FindReferences(nested.Id));
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void ClosedHierarchies_PreserveBaseIdentityAndExhaustiveSwitch(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public closed record class GateState;
            public record class Closed : GateState;
            public record class Open(float Percent) : GateState;
            public class Runner
            {
                public string Describe(GateState state) => state switch
                {
                    Closed => "closed",
                    Open(var percent) => percent.ToString()
                };
            }
            """, (graph, analyzer) =>
        {
            var gate = Assert.Single(graph.Symbols, symbol => symbol.Name == "GateState");
            var open = Assert.Single(graph.Symbols, symbol => symbol.Name == "Open");
            Assert.True(gate.IsAbstract);
            Assert.Contains(graph.Edges, edge => edge.SourceId == open.Id && edge.TargetId == gate.Id);
            Assert.DoesNotContain(analyzer.Compilations!["Test"].GetDiagnostics(), diagnostic => diagnostic.Id == "CS8509");
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void ExtensionIndexers_KeepDeclarationBindingAndReferences(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            using System.Collections.Generic;
            using System.Linq;
            namespace Features;
            public static class SequenceIndexer
            {
                extension(IEnumerable<int> sequence)
                {
                    public int this[int index] => sequence.ElementAt(index);
                }
            }
            public class Runner
            {
                public int Read(IEnumerable<int> values) => values[2];
                public int ReadNullable(IEnumerable<int>? values) => values?[2] ?? 0;
            }
            """, (graph, analyzer) =>
        {
            var indexer = Assert.Single(graph.Symbols, symbol => symbol.Properties.ContainsKey("isIndexer"));
            var read = Assert.Single(graph.Symbols, symbol => symbol.Name == "Read");
            Assert.Contains(graph.Edges, edge => edge.SourceId == read.Id && edge.TargetId == indexer.Id);
            var nullableRead = Assert.Single(graph.Symbols, symbol => symbol.Name == "ReadNullable");
            Assert.Contains(graph.Edges, edge => edge.SourceId == nullableRead.Id && edge.TargetId == indexer.Id);
            var compilation = analyzer.Compilations!["Test"];
            Assert.NotNull(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(indexer.Id)));
            using var host = new RoslynCompilationHost(analyzer.Compilations!, idVersion: version);
            Assert.NotEmpty(host.FindReferences(indexer.Id));

            using var image = new MemoryStream();
            Assert.True(compilation.Emit(image).Success);
            var consumerTree = CSharpSyntaxTree.ParseText("""
                using System.Collections.Generic;
                using Features;
                public class Consumer { public int Read(IEnumerable<int> values) => values[1]; }
                """, new CSharpParseOptions(LanguageVersion.CSharp15));
            var consumer = CSharpCompilation.Create("Consumer", new[] { consumerTree },
                BclReferenceLoader.References.Value.Append(MetadataReference.CreateFromImage(image.ToArray())),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(consumer.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var extractor = new RoslynEdgeExtractor(version) { KnownModuleAssemblies = new HashSet<string> { "Test" } };
            Assert.Contains(extractor.Extract(consumer.GetSemanticModel(consumerTree), consumerTree.GetRoot()),
                edge => edge.TargetId == indexer.Id && edge.Kind == EdgeKind.References);
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Indexers_BindOverloadsReadsWritesAndConditionalAccess(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            namespace Features;
            public class Box
            {
                public int this[int index] { get => index; set { } }
                public int this[string key] => key.Length;
            }
            public class Runner
            {
                public int Read(Box box) => box[1];
                public void Write(Box box) { box[1] = 4; }
                public int Conditional(Box? box) => box?[1] ?? 0;
                public int Overload(Box box) => box["key"];
                public int Array(int[] values) => values[1];
            }
            """, (graph, analyzer) =>
        {
            var properties = analyzer.Compilations!["Test"].GetTypeByMetadataName("Features.Box")!
                .GetMembers().OfType<IPropertySymbol>().Where(property => property.IsIndexer).ToArray();
            var integerId = CanonicalSymbolFormat.BuildPropertyId(
                Assert.Single(properties, property => property.Parameters[0].Type.SpecialType == SpecialType.System_Int32), version);
            var stringId = CanonicalSymbolFormat.BuildPropertyId(
                Assert.Single(properties, property => property.Parameters[0].Type.SpecialType == SpecialType.System_String), version);
            foreach (var name in new[] { "Read", "Write", "Conditional", "Overload" })
            {
                var method = Assert.Single(graph.Symbols, symbol => symbol.Name == name);
                var edge = Assert.Single(graph.Edges, edge => edge.SourceId == method.Id && edge.TargetId == (name == "Overload" ? stringId : integerId));
                Assert.Equal(EdgeKind.References, edge.Kind);
                Assert.Equal(method.Id, edge.CallSite!.ContainingSymbolId);
                Assert.Equal("Test.cs", edge.CallSite.FilePath);
            }
            var array = Assert.Single(graph.Symbols, symbol => symbol.Name == "Array");
            Assert.DoesNotContain(graph.Edges, edge => edge.SourceId == array.Id && (edge.TargetId == integerId || edge.TargetId == stringId));
        }, version);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void CollectionArgumentsAndLabeledJumps_CompileAndKeepCalls(CanonicalSymbolIdVersion version)
    {
        WithWorkspace("""
            using System.Collections.Generic;
            namespace Features;
            public class Runner
            {
                public static int Capacity() => 8;
                public int Run()
                {
                    List<int> values = [with(capacity: Capacity()), 1, 2];
                    outer: for (int row = 0; row < 3; row++)
                    {
                        for (int column = 0; column < 3; column++)
                        {
                            if (column == 1) continue outer;
                            if (row == 2) break outer;
                            values.Add(column);
                        }
                    }
                    return values.Count;
                }
            }
            """, (graph, _) =>
        {
            var run = Assert.Single(graph.Symbols, symbol => symbol.Name == "Run");
            var capacity = Assert.Single(graph.Symbols, symbol => symbol.Name == "Capacity");
            Assert.Contains(graph.Edges, edge => edge.SourceId == run.Id && edge.TargetId == capacity.Id && edge.Kind == EdgeKind.Calls);
        }, version);
    }

    private static void WithWorkspace(string source, Action<SemanticGraph, RoslynWorkspaceAnalyzer> verify,
        CanonicalSymbolIdVersion version)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-csharp15-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Test.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable>
                </PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(root, "Test.cs"), source);
            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem(), idVersion: version);
            var graph = analyzer.AnalyzeWorkspace(root, new AnalysisConfig { RetainCompilations = true });
            var compilation = analyzer.Compilations!["Test"];
            Assert.All(compilation.SyntaxTrees, tree =>
                Assert.Equal(LanguageVersion.CSharp15, ((CSharpParseOptions)tree.Options).LanguageVersion));
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(error => error.ToString())));
            using var image = new MemoryStream();
            var emit = compilation.Emit(image);
            Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
            verify(graph, analyzer);
        }
        finally { Directory.Delete(root, true); }
    }
}
