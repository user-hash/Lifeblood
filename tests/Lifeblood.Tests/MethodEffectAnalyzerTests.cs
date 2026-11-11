using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class MethodEffectAnalyzerTests
{
    private const string Source = """
        using System;
        namespace Effects
        {
            public sealed class Probe
            {
                private int _state;
                public void Empty() { }
                public void Sink() { _state++; }
                public void Middle() => Sink();
                public void Root() => Middle();
                public void Elements(int[] buffer) { buffer[0]++; buffer[1] = 1; }
                public Action Deferred() => () => Sink();
                public void Local() { void Run() => Sink(); Run(); }
                public void DelegateBinding() { Action callback = Sink; callback(); }
                public void Dynamic(dynamic target) => target.Run();
                public unsafe int Pointer(delegate*<int, int> target) => target(1);
                [System.Runtime.InteropServices.DllImport("fixture")] public static extern void Native();
                public void External() => Native();
                public int Property => _state++;
                public int ReadProperty() => Property;
                public void CycleA() => CycleB();
                public void CycleB() => CycleA();
            }
        }
        """;

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void DirectTransitiveAndLocalCallsReachBodiesButDeferredCreationDoesNot(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var report = fixture.Analyze(new[] { "Root", "Local", "Deferred" });
        var root = fixture.Method(report, "Root");
        Assert.Equal(3, root.ReachedCallableIds.Length);
        Assert.Equal(2, root.CallCount);
        Assert.Contains(root.MemberAccesses, access => access.Kind == OperationFactKind.MemberWrite);
        Assert.Equal(new[] { OperationAccessMode.Read, OperationAccessMode.Write },
            Assert.Single(root.MemberAccesses, access => access.Kind == OperationFactKind.MemberWrite).AccessModes);
        var local = fixture.Method(report, "Local");
        Assert.Equal(3, local.ReachedCallableIds.Length);
        Assert.Contains(local.MemberAccesses, access => access.Kind == OperationFactKind.MemberWrite);
        Assert.Equal(0, local.DeferredCallableCount);
        var deferred = fixture.Method(report, "Deferred");
        Assert.Single(deferred.ReachedCallableIds);
        Assert.Empty(deferred.MemberAccesses);
        Assert.Equal(0, deferred.CallCount);
        Assert.Equal(1, deferred.DeferredCallableCount);
        Assert.NotEmpty(deferred.CallbackBindings);
        Assert.Equal(0, report.ScanReceipt.AdditionalSemanticBaseCount);
        Assert.Equal(0, report.ScanReceipt.CompiledModuleCount);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void DelegateBindingDoesNotResolveInvocationAndIndirectNativeAndAccessorEffectsStayUnknown(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var report = fixture.Analyze(new[] { "DelegateBinding", "Dynamic", "Pointer", "External", "ReadProperty" });
        foreach (var name in new[] { "DelegateBinding", "Dynamic", "Pointer" })
        {
            var method = fixture.Method(report, name);
            Assert.Single(method.ReachedCallableIds);
            Assert.Contains(method.UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.IndirectDispatch);
            Assert.DoesNotContain(method.MemberAccesses, access => access.Kind == OperationFactKind.MemberWrite);
        }
        Assert.NotEmpty(fixture.Method(report, "DelegateBinding").CallbackBindings);
        Assert.Contains(fixture.Method(report, "External").UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.ExternalImplementation);
        Assert.Contains(fixture.Method(report, "ReadProperty").UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.PropertyOrEventAccessorEffects);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void EmptyBodyAndUnavailableBodyRemainDistinctAndCyclesTerminate(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var report = fixture.Analyze(new[] { "Empty", "Native", "CycleA" });
        var empty = fixture.Method(report, "Empty");
        Assert.Equal(0, empty.ObservedFactCount);
        Assert.Equal(OperationCallableBodyKind.SourceBody, Assert.Single(empty.CallableDeclarations).Declaration.BodyKind);
        Assert.Contains(fixture.Method(report, "Native").UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.SourceBodyUnavailable);
        Assert.Equal(2, fixture.Method(report, "CycleA").ReachedCallableIds.Length);
        Assert.Contains(report.Limitations, limit => limit.Contains("cannot prove absence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void EveryScopeLimitStaysExplicitAndEvidenceCapsDoNotChangeObservedCounts(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var depth = Assert.Single(fixture.Analyze(new[] { "Root" }, depth: 0).Methods);
        Assert.Contains(depth.UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.DepthLimit);
        var member = Assert.Single(fixture.Analyze(new[] { "Root" }, methods: 1).Methods);
        Assert.Contains(member.UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.CandidateScopeTruncated);
        Assert.Contains(member.UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.MethodLimit);
        var scan = Assert.Single(fixture.Analyze(new[] { "Root" }, maxFacts: 1).Methods);
        Assert.Contains(scan.UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.ScanTruncated);
        var full = Assert.Single(fixture.Analyze(new[] { "Root" }).Methods);
        var compact = Assert.Single(fixture.Analyze(new[] { "Root" }, evidence: 0).Methods);
        Assert.Equal(full.ObservedFactCount, compact.ObservedFactCount);
        Assert.Equal(full.CallCount, compact.CallCount);
        Assert.Equal(full.MemberAccessCount, compact.MemberAccessCount);
        Assert.Empty(compact.Calls);
        Assert.True(compact.EvidenceTruncated);
    }

    [Fact]
    public void MissingDeclarationNeverBecomesAnEmptyBodyAndCancellationIsObserved()
    {
        using var fixture = new Fixture(CanonicalSymbolIdVersion.AssemblyQualifiedV2);
        var report = fixture.Analyze(new[] { "Root" }, removeDeclarations: true);
        Assert.Contains(Assert.Single(report.Methods).UnknownReasons, reason => reason.Kind == MethodEffectUnknownReason.RootDeclarationUnavailable);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Analyze(new[] { "Root" }, cancellation: canceled.Token));
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void ElementAccessModesDistinguishReadModifyWriteFromAssignment(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var method = Assert.Single(fixture.Analyze(new[] { "Elements" }).Methods);
        var elements = method.MemberAccesses.Where(access => access.Kind == OperationFactKind.ElementAccess).ToArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal(new[] { OperationAccessMode.Read, OperationAccessMode.Write }, elements[0].AccessModes);
        Assert.Equal(new[] { OperationAccessMode.Write }, elements[1].AccessModes);
        Assert.All(elements, element => { Assert.Null(element.TargetSymbolId); Assert.NotNull(element.ReceiverValueId); });
    }

    [Fact]
    public void InvalidBudgetsAndRootSetsRejectBeforePlanning()
    {
        Assert.Throws<ArgumentException>(() => MethodEffectAnalyzer.Validate(new MethodEffectRequest()));
        Assert.Throws<ArgumentException>(() => MethodEffectAnalyzer.Validate(new MethodEffectRequest { RootSymbolIds = new[] { "same", "same" } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MethodEffectAnalyzer.Validate(new MethodEffectRequest { RootSymbolIds = new[] { "root" }, MaxDepth = 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MethodEffectAnalyzer.Validate(new MethodEffectRequest { RootSymbolIds = new[] { "root" }, MaxFacts = 100_001 }));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SemanticGraph _graph;
        private readonly RoslynCompilationHost _host;

        public Fixture(CanonicalSymbolIdVersion policy)
        {
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "Microsoft.CSharp.dll", "System.Linq.Expressions.dll", "System.Runtime.InteropServices.dll" }
                .Select(name => MetadataReference.CreateFromFile(Path.Combine(runtime, name)));
            var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp9), "Effects.cs");
            var compilation = CSharpCompilation.Create("Effects", new[] { tree }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var emit = compilation.Emit(Stream.Null);
            Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
            var model = compilation.GetSemanticModel(tree);
            _graph = new GraphBuilder()
                .AddSymbols(new RoslynSymbolExtractor(policy).Extract(model, tree.GetRoot(), "Effects.cs", "file:Effects.cs"))
                .AddEdges(new RoslynEdgeExtractor(policy).Extract(model, tree.GetRoot(), "Effects.cs"))
                .Build();
            _host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["Effects"] = compilation }, retainedProfileName: "Editor", idVersion: policy);
        }

        private string Id(string name) => Assert.Single(_graph.Symbols, symbol => symbol.Name == name).Id;
        public MethodEffectSummary Method(MethodEffectReport report, string name) => Assert.Single(report.Methods, method => method.RootSymbolId == Id(name));

        public MethodEffectReport Analyze(string[] names, int depth = 4, int methods = 128, int maxFacts = 10_000, int evidence = 16,
            bool removeDeclarations = false, CancellationToken cancellation = default)
        {
            var request = new MethodEffectRequest { RootSymbolIds = names.Select(Id).ToArray(), ProfileScope = "Editor",
                MaxDepth = depth, MaxMethods = methods, MaxFacts = maxFacts, MaxEvidencePerMethod = evidence };
            var plan = MethodEffectAnalyzer.Plan(_graph, request);
            var facts = new List<OperationFact>();
            var receipt = ((IOperationFactProvider)_host).ScanOperationFacts(new OperationFactQuery
            {
                IncludeCallableDeclarations = true, IncludeImplicit = true,
                ContainingSymbolIds = plan.Matches.Select(match => match.ContainingSymbolId).Distinct().ToArray(),
                MaxFacts = maxFacts,
            }, fact => { facts.Add(fact); return true; }, cancellation);
            return MethodEffectAnalyzer.Analyze(_graph, request, plan,
                removeDeclarations ? facts.Where(fact => fact.CallableDeclaration == null).ToArray() : facts,
                receipt, cancellation);
        }

        public void Dispose() => _host.Dispose();
    }
}
