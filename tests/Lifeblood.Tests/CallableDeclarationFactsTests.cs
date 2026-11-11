using System.Text;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CallableDeclarationFactsTests
{
    private const string Source = """
        using System;
        public abstract class Bodies
        {
            public void Empty() { }
            public abstract void Abstract();
            [System.Runtime.InteropServices.DllImport("fixture")] public static extern void Native();
            public int Auto { get; set; }
            public int Expression => 42;
            public Action Deferred() => () => { };
            public void Local() { void Run() { } Run(); }
        }
        """;

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void DeclarationsDistinguishEmptyMissingAndDeferredBodiesWithoutChangingExistingFacts(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(Source, policy);
        var ordinary = fixture.Scan(new OperationFactQuery());
        var expanded = fixture.Scan(new OperationFactQuery { IncludeCallableDeclarations = true });
        Assert.DoesNotContain(ordinary.Facts, fact => fact.CallableDeclaration != null);
        Assert.Equal(Serialize(ordinary.Facts), Serialize(expanded.Facts.Where(fact => fact.CallableDeclaration == null)));
        var declarations = expanded.Facts.Where(fact => fact.CallableDeclaration != null).ToArray();
        Assert.Equal(OperationCallableBodyKind.SourceBody, fixture.Declaration(declarations, "Empty").CallableDeclaration!.BodyKind);
        Assert.Equal(OperationCallableBodyKind.NoSourceBody, fixture.Declaration(declarations, "Abstract").CallableDeclaration!.BodyKind);
        Assert.True(fixture.Declaration(declarations, "Native").CallableDeclaration!.IsExternal);
        var deferred = declarations.Where(fact => fact.CallableDeclaration!.IsDeferred).ToArray();
        Assert.Equal(2, deferred.Length);
        Assert.All(deferred, fact =>
        {
            Assert.Equal(OperationCallableBodyKind.SourceBody, fact.CallableDeclaration!.BodyKind);
            Assert.NotNull(fact.CallableDeclaration.ParentCallableId);
            Assert.NotEqual(fact.ContainingCallableId, fact.CallableDeclaration.ParentCallableId);
            Assert.False(fact.IsImplicit);
        });
        Assert.All(declarations, fact => Assert.False(fact.CallableDeclaration!.HasBindingErrors));
        Assert.Equal(0, expanded.Receipt.AdditionalSemanticBaseCount);
        Assert.Equal(0, expanded.Receipt.CompiledModuleCount);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void DeclarationSelectionPreservesProjectionAndSharesTheHardFactBudget(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(Source, policy);
        var query = new OperationFactQuery { IncludeCallableDeclarations = true, IncludeKinds = new[] { OperationFactKind.CallableDeclaration } };
        var full = fixture.Scan(query);
        var empty = fixture.Declaration(full.Facts, "Empty");
        var selected = fixture.Scan(new OperationFactQuery
        {
            IncludeCallableDeclarations = true,
            ContainingSymbolIds = new[] { empty.ContainingSymbolId },
            Selectors = new[] { new OperationFactSelector { IncludeKinds = new[] { OperationFactKind.CallableDeclaration }, TargetSymbolIds = new[] { empty.TargetSymbolId! } } },
        });
        Assert.Equal(Serialize(new[] { empty }), Serialize(selected.Facts));
        var bounded = fixture.Scan(new OperationFactQuery { IncludeCallableDeclarations = true, MaxFacts = 1 });
        Assert.Single(bounded.Facts);
        Assert.True(bounded.Receipt.Truncated);
        Assert.Equal(1, bounded.Receipt.EmittedFactCount);
        Assert.Equal(0, bounded.Receipt.ObservedOperationCount);
        var stopped = fixture.Scan(query, stop: true);
        Assert.Single(stopped.Facts);
        Assert.True(stopped.Receipt.StoppedByConsumer);
        Assert.False(stopped.Receipt.Truncated);
    }

    [Fact]
    public void ChecksumDescribesRetainedCompilerTextAndBindingErrorsRemainExplicit()
    {
        using var fixture = new Fixture(Source.Replace("public void Empty() { }", "public void Empty() { Missing(); }", StringComparison.Ordinal),
            CanonicalSymbolIdVersion.AssemblyQualifiedV2, expectErrors: true);
        var facts = fixture.Scan(new OperationFactQuery { IncludeCallableDeclarations = true }).Facts;
        var declaration = fixture.Declaration(facts, "Empty");
        Assert.True(declaration.CallableDeclaration!.HasBindingErrors);
        Assert.Equal("Sha256", declaration.CallableDeclaration.SourceChecksumAlgorithm);
        Assert.Equal(Convert.ToHexString(fixture.Text.GetChecksum().AsSpan()).ToLowerInvariant(), declaration.CallableDeclaration.SourceChecksum);
        Assert.Equal("utf-8", declaration.CallableDeclaration.SourceEncoding);
        Assert.All(facts.Where(fact => fact.CallableDeclaration != null), fact =>
            Assert.Equal(declaration.CallableDeclaration.SourceChecksum, fact.CallableDeclaration!.SourceChecksum));
    }

    [Fact]
    public void CancellationIsObservedBeforeDeclarationMaterialization()
    {
        using var fixture = new Fixture(Source, CanonicalSymbolIdVersion.LegacyV1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ((IOperationFactProvider)fixture.Host).ScanOperationFacts(
            new OperationFactQuery { IncludeCallableDeclarations = true }, _ => true, cancellation.Token));
    }

    private static string Serialize(IEnumerable<OperationFact> facts) => System.Text.Json.JsonSerializer.Serialize(facts.ToArray());

    private sealed class Fixture : IDisposable
    {
        private readonly Lifeblood.Domain.Graph.Symbol[] _symbols;
        public SourceText Text { get; }
        public RoslynCompilationHost Host { get; }

        public Fixture(string source, CanonicalSymbolIdVersion policy, bool expectErrors = false)
        {
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Runtime.InteropServices.dll" }
                .Select(name => MetadataReference.CreateFromFile(Path.Combine(runtime, name)));
            Text = SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256);
            var tree = CSharpSyntaxTree.ParseText(Text, new CSharpParseOptions(LanguageVersion.CSharp9), "Bodies.cs");
            var compilation = CSharpCompilation.Create("Bodies", new[] { tree }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.Equal(expectErrors, !compilation.Emit(Stream.Null).Success);
            _symbols = new RoslynSymbolExtractor(policy).Extract(compilation.GetSemanticModel(tree), tree.GetRoot(), "Bodies.cs", "file:Bodies.cs").ToArray();
            Host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["Bodies"] = compilation }, retainedProfileName: "Editor", idVersion: policy);
        }

        public OperationFact Declaration(IEnumerable<OperationFact> facts, string name)
        {
            var id = Assert.Single(_symbols, symbol => symbol.Name == name).Id;
            return Assert.Single(facts, fact => fact.Kind == OperationFactKind.CallableDeclaration && fact.TargetSymbolId == id);
        }

        public (OperationFact[] Facts, OperationFactScanReceipt Receipt) Scan(OperationFactQuery query, bool stop = false)
        {
            var facts = new List<OperationFact>();
            var receipt = ((IOperationFactProvider)Host).ScanOperationFacts(query, fact => { facts.Add(fact); return !stop; });
            return (facts.ToArray(), receipt);
        }

        public void Dispose() => Host.Dispose();
    }
}
