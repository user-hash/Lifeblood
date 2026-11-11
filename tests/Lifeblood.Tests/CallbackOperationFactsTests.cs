using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CallbackOperationFactsTests
{
    private const string Source = """
        using System;
        namespace Callbacks
        {
            public interface IWorker { void Work(); }
            public class Worker { public virtual void Work() { } }
            public class Base { public virtual void Execute() { } }
            public sealed class Probe : Base
            {
                private int _state;
                public event Action Changed;
                public static void Sink() { }
                public void LocalKnown() { Action callback = Sink; callback(); }
                public Action Deferred(int gain) => () => { _state += gain; Sink(); };
                public void LocalOne() { void Run() => Sink(); Run(); }
                public void LocalTwo() { void Run() => _state++; Run(); }
                public void Subscribe() { Changed += Sink; Changed -= Sink; Changed += () => _state++; }
                public void Raise() => Changed?.Invoke();
                public void Dynamic(dynamic target, int value) => target.Work(value);
                public unsafe int Pointer(delegate*<int, int> action, int value) => action(value);
                public void Direct() => Sink();
                public void Interface(IWorker worker) => worker.Work();
                public void Virtual(Worker worker) => worker.Work();
                public void BaseDispatch() => base.Execute();
                [System.Runtime.InteropServices.DllImport("CallbackFixtureNative")] private static extern void External();
                public void Native() => External();
                public Action<int>[] TwoCallbacks() => new Action<int>[] { value => _state = value, value => _state += value };
            }
        }
        """;

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_IndirectCallsPreserveUnknownTargetReceiverAndArguments(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var facts = fixture.Scan();
        foreach (var owner in new[] { "Dynamic", "Pointer" })
        {
            var call = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id(owner));
            Assert.Null(call.TargetSymbolId);
            Assert.Null(call.TargetCallableId);
            Assert.Null(call.TargetIsExternal);
            Assert.Equal(owner == "Dynamic" ? OperationInvocationKind.Dynamic : OperationInvocationKind.FunctionPointer, call.InvocationKind);
            var receiver = Assert.Single(call.Inputs, input => input.Role == OperationInputRole.Receiver);
            Assert.NotEmpty(receiver.Value.SourceSymbolIds);
            var argument = Assert.Single(call.Inputs, input => input.Role == OperationInputRole.Argument);
            Assert.Equal(0, argument.Ordinal);
            Assert.True(argument.AuthorSupplied);
            Assert.Null(argument.ParameterId);
            Assert.Contains(argument.Value.SourceSymbolIds, id => id.EndsWith(":value", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_LocalDelegateInitializationLinksTheInvokedLocalToItsBoundMethod(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var facts = fixture.Scan(includeImplicit: false);
        var assignment = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Assignment && fact.ContainingSymbolId == fixture.Id("LocalKnown"));
        var value = Assert.Single(assignment.Inputs, input => input.Role == OperationInputRole.Value).Value;
        Assert.Equal(OperationValueKind.MethodGroup, value.Kind);
        Assert.Equal(fixture.Id("Sink"), value.SymbolId);
        var invocation = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("LocalKnown"));
        Assert.Equal(assignment.TargetSymbolId, Assert.Single(invocation.Inputs).Value.SymbolId);
        Assert.False(assignment.IsImplicit);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_EventChangesKeepHandlerBindingsAndTheConditionalReceiver(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var facts = fixture.Scan(includeImplicit: false);
        var changes = facts.Where(fact => fact.Kind == OperationFactKind.Assignment && fact.TargetSymbolId == fixture.Id("Changed")).ToArray();
        Assert.Equal(new[] { "Add", "Remove", "Add" }, changes.Select(fact => fact.Operator));
        Assert.All(changes.Take(2), change => Assert.Equal(fixture.Id("Sink"), Assert.Single(change.Inputs, input => input.Role == OperationInputRole.Value).Value.SymbolId));
        Assert.Equal(OperationValueKind.Lambda, Assert.Single(changes[2].Inputs, input => input.Role == OperationInputRole.Value).Value.Kind);
        Assert.NotNull(Assert.Single(changes[2].Inputs, input => input.Role == OperationInputRole.Value).Value.CallableId);
        var raised = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("Raise"));
        Assert.Equal(OperationInvocationKind.Delegate, raised.InvocationKind);
        Assert.Null(raised.TargetCallableId);
        Assert.Equal(fixture.Id("Changed"), Assert.Single(raised.Inputs, input => input.Role == OperationInputRole.Receiver).Value.SymbolId);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_DispatchAndExternalDeclarationsDoNotImplyConcreteRuntimeTargets(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var facts = fixture.Scan();
        foreach (var (owner, kind) in new[] { ("Direct", OperationInvocationKind.Direct), ("Interface", OperationInvocationKind.Interface),
                     ("Virtual", OperationInvocationKind.Virtual), ("BaseDispatch", OperationInvocationKind.Direct), ("Native", OperationInvocationKind.Direct) })
        {
            var call = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id(owner));
            Assert.Equal(kind, call.InvocationKind);
            Assert.NotNull(call.TargetSymbolId);
            Assert.NotNull(call.TargetCallableId);
            Assert.Equal(owner == "Native", call.TargetIsExternal);
        }
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_DeferredBodiesAndSameNamedLocalFunctionsHaveDistinctCallableOwners(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var facts = fixture.Scan();
        var one = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("LocalOne") && fact.InvocationKind == OperationInvocationKind.LocalFunction);
        var two = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("LocalTwo") && fact.InvocationKind == OperationInvocationKind.LocalFunction);
        Assert.NotEqual(one.TargetCallableId, two.TargetCallableId);
        var localBody = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("LocalOne") && fact.ControlContexts.Any(context => context.Kind == OperationControlContextKind.LocalFunction));
        Assert.Equal(one.TargetCallableId, localBody.ContainingCallableId);
        Assert.NotEqual(one.ContainingCallableId, localBody.ContainingCallableId);
        var deferredCall = Assert.Single(facts, fact => fact.Kind == OperationFactKind.Call && fact.ContainingSymbolId == fixture.Id("Deferred"));
        var creation = Assert.Single(facts, fact => fact.Kind == OperationFactKind.DelegateCreation && fact.ContainingSymbolId == fixture.Id("Deferred"));
        Assert.Contains(deferredCall.ControlContexts, context => context.Kind == OperationControlContextKind.AnonymousFunction);
        Assert.Equal(Assert.Single(creation.Inputs).Value.CallableId, deferredCall.ContainingCallableId);
        Assert.NotEqual(creation.ContainingCallableId, deferredCall.ContainingCallableId);
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void Scan_LambdaParametersKeepTheirDistinctLexicalOwners(CanonicalSymbolIdVersion policy)
    {
        using var fixture = new Fixture(policy);
        var writes = fixture.Scan().Where(fact => fact.Kind == OperationFactKind.Assignment && fact.ContainingSymbolId == fixture.Id("TwoCallbacks")).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(2, writes.Select(fact => fact.ContainingCallableId).Distinct().Count());
        Assert.Equal(2, writes.Select(fact => Assert.Single(fact.Inputs, input => input.Role == OperationInputRole.Value).Value.SymbolId).Distinct().Count());
    }

    [Fact]
    public void CallableKeysRemainTheSameAcrossDeclarationSchemas()
    {
        using var legacy = new Fixture(CanonicalSymbolIdVersion.LegacyV1);
        using var canonical = new Fixture(CanonicalSymbolIdVersion.AssemblyQualifiedV2);
        var legacyFacts = legacy.Scan();
        var first = legacyFacts.Select(fact => (fact.Id, fact.ContainingCallableId, fact.TargetCallableId)).ToArray();
        var second = canonical.Scan().Select(fact => (fact.Id, fact.ContainingCallableId, fact.TargetCallableId)).ToArray();
        Assert.Equal(first, second);
        var expected = legacyFacts.Where(fact => fact.ContainingSymbolId == legacy.Id("LocalOne")).ToArray();
        var selected = legacy.Scan(containingSymbolId: legacy.Id("LocalOne"));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(selected));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RoslynCompilationHost _host;
        private readonly Lifeblood.Domain.Graph.Symbol[] _symbols;

        public Fixture(CanonicalSymbolIdVersion policy)
        {
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll", "Microsoft.CSharp.dll", "System.Linq.Expressions.dll", "System.Runtime.InteropServices.dll" }
                .Select(name => MetadataReference.CreateFromFile(Path.Combine(runtime, name)));
            var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp9), "Callbacks.cs");
            var compilation = CSharpCompilation.Create("Callbacks", new[] { tree }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var emitted = compilation.Emit(Stream.Null);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
            _symbols = new RoslynSymbolExtractor(policy).Extract(compilation.GetSemanticModel(tree), tree.GetRoot(), "Callbacks.cs", "file:Callbacks.cs").ToArray();
            _host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["Callbacks"] = compilation }, retainedProfileName: "Editor", idVersion: policy);
        }

        public string Id(string name) => Assert.Single(_symbols, symbol => symbol.Name == name).Id;

        public OperationFact[] Scan(bool includeImplicit = true, string? containingSymbolId = null)
        {
            var facts = new List<OperationFact>();
            var receipt = ((IOperationFactProvider)_host).ScanOperationFacts(new OperationFactQuery
                { IncludeImplicit = includeImplicit, MaxFacts = 1000, ContainingSymbolIds = containingSymbolId == null ? null : new[] { containingSymbolId } },
                fact => { facts.Add(fact); return true; });
            Assert.False(receipt.Truncated);
            Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
            Assert.Equal(0, receipt.CompiledModuleCount);
            return facts.ToArray();
        }

        public void Dispose() => _host.Dispose();
    }
}
