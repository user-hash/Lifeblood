using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
