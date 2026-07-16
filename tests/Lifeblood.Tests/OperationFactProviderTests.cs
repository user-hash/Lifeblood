using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-OPERATION-FACTS-001. The C# adapter emits one bounded neutral fact
/// stream without adding graph edges or retaining a second semantic base.
/// </summary>
public sealed class OperationFactProviderTests
{
    private const string Source = """
        using System;
        namespace Acme;

        public sealed class Engine
        {
            private float _state;

            private void Sink(float value) { }

            public void Run(float input, bool enabled)
            {
                for (var i = 0; i < 4; i++)
                {
                    Sink(Math.Clamp(input, 0f, 1f));
                }

                if (enabled)
                    _state = (float)(input * 2.0);
            }

            public int[] Allocate(int count) => new int[count];
            public void Twice() { Sink(1f); Sink(2f); }
        }
        """;

    [Fact]
    public void Scan_CallCarriesBoundArgumentValueOriginAndLoopContext()
    {
        using var host = HostWith(Source);
        var (facts, receipt) = Scan(host, new OperationFactQuery());

        var call = Assert.Single(facts, fact =>
            fact.Kind == OperationFactKind.Call
            && fact.TargetSymbolId == "method:Acme.Engine.Sink(float)"
            && fact.ContainingSymbolId == "method:Acme.Engine.Run(float,bool)");
        var argument = Assert.Single(call.Inputs, input => input.Role == OperationInputRole.Argument);

        Assert.Equal("value", argument.ParameterName);
        Assert.Equal(0, argument.Ordinal);
        Assert.True(argument.AuthorSupplied);
        Assert.Equal(OperationValueKind.Invocation, argument.Value.Kind);
        Assert.Contains(
            "parameter:method:Acme.Engine.Run(float,bool)#0:input",
            argument.Value.SourceSymbolIds);
        Assert.Contains(call.ControlContexts, context =>
            context.Kind == OperationControlContextKind.Loop
            && context.Predicates.Any(predicate =>
                predicate.Operator == "LessThan"
                && predicate.SourceSymbolIds.Any(id => id.EndsWith(":i", StringComparison.Ordinal))));
        Assert.Equal("Editor", receipt.ProfileScope);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
    }

    [Fact]
    public void Scan_AssignmentCarriesTargetValueConversionAndBranchContext()
    {
        using var host = HostWith(Source);
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            IncludeKinds = new[] { OperationFactKind.Assignment },
        });

        var assignment = Assert.Single(facts, fact =>
            fact.TargetSymbolId == "field:Acme.Engine._state");
        var value = Assert.Single(assignment.Inputs, input => input.Role == OperationInputRole.Value).Value;

        Assert.Contains(
            "parameter:method:Acme.Engine.Run(float,bool)#0:input",
            value.SourceSymbolIds);
        Assert.Contains("float", value.ConversionTypes);
        Assert.Contains(assignment.ControlContexts, context =>
            context.Kind == OperationControlContextKind.Branch
            && context.Condition == "enabled"
            && context.ConditionValue != null
            && context.ConditionValue.SourceSymbolIds.Contains(
                "parameter:method:Acme.Engine.Run(float,bool)#1:enabled"));
    }

    [Fact]
    public void Scan_ArrayCreationCarriesDimensionOrigin()
    {
        using var host = HostWith(Source);
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            IncludeKinds = new[] { OperationFactKind.ArrayCreation },
        });

        var allocation = Assert.Single(facts);
        var dimension = Assert.Single(allocation.Inputs);
        Assert.Equal(OperationInputRole.Argument, dimension.Role);
        Assert.Contains(
            "parameter:method:Acme.Engine.Allocate(int)#0:count",
            dimension.Value.SourceSymbolIds);
    }

    [Fact]
    public void Scan_MaxFactsTruncatesOnlyWhenAnotherMatchingFactExists()
    {
        using var host = HostWith(Source);
        var (facts, receipt) = Scan(host, new OperationFactQuery
        {
            ContainingSymbolIds = new[] { "method:Acme.Engine.Twice()" },
            IncludeKinds = new[] { OperationFactKind.Call },
            MaxFacts = 1,
        });

        Assert.Single(facts);
        Assert.True(receipt.Truncated);
        Assert.False(receipt.StoppedByConsumer);
        Assert.Equal(1, receipt.EmittedFactCount);
    }

    [Fact]
    public void Scan_ConsumerCanStopWithoutClaimingTruncation()
    {
        using var host = HostWith(Source);
        var receipt = ((IOperationFactProvider)host).ScanOperationFacts(
            new OperationFactQuery(),
            _ => false);

        Assert.True(receipt.StoppedByConsumer);
        Assert.False(receipt.Truncated);
        Assert.Equal(1, receipt.EmittedFactCount);
    }

    [Fact]
    public void Scan_ProfileMismatchFailsLoudlyAndNamesAvailableProfiles()
    {
        using var host = HostWith(Source);
        var error = Assert.Throws<ArgumentException>(() =>
            ((IOperationFactProvider)host).ScanOperationFacts(
                new OperationFactQuery { ProfileScope = "Player" },
                _ => true));

        Assert.Contains("retained operation profile 'Editor'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Editor, Player", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_IdenticalInputProducesStableFactOrderAndIds()
    {
        using var host = HostWith(Source);
        var (first, _) = Scan(host, new OperationFactQuery());
        var (second, _) = Scan(host, new OperationFactQuery());

        Assert.Equal(first.Select(fact => fact.Id), second.Select(fact => fact.Id));
        Assert.Equal(first.Select(fact => fact.Kind), second.Select(fact => fact.Kind));
    }

    [Fact]
    public void Scan_FactIdsDoNotDependOnEarlierFileFilters()
    {
        const string firstSource = "namespace Acme; public sealed class First { public void Run() { System.GC.KeepAlive(1); } }";
        const string secondSource = "namespace Acme; public sealed class Second { public void Run() { System.GC.KeepAlive(2); } }";
        using var host = HostWithSources(
            ("First.cs", firstSource),
            ("Second.cs", secondSource));

        var (allFacts, _) = Scan(host, new OperationFactQuery
        {
            IncludeKinds = new[] { OperationFactKind.Call },
        });
        var (filteredFacts, _) = Scan(host, new OperationFactQuery
        {
            FilePaths = new[] { "Second.cs" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        var secondFact = Assert.Single(allFacts, fact => fact.Source.FilePath.EndsWith("Second.cs", StringComparison.Ordinal));
        Assert.Equal(secondFact.Id, Assert.Single(filteredFacts).Id);
    }

    [Fact]
    public void Scan_TargetFilterEmitsOnlyRequestedBoundTargets()
    {
        using var host = HostWith(Source);
        var (facts, receipt) = Scan(host, new OperationFactQuery
        {
            TargetSymbolIds = new[] { "method:Acme.Engine.Sink(float)" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        Assert.Equal(3, facts.Length);
        Assert.All(facts, fact => Assert.Equal("method:Acme.Engine.Sink(float)", fact.TargetSymbolId));
        Assert.Equal(3, receipt.EmittedFactCount);
    }

    private static (OperationFact[] Facts, OperationFactScanReceipt Receipt) Scan(
        RoslynCompilationHost host,
        OperationFactQuery query)
    {
        var facts = new List<OperationFact>();
        var receipt = ((IOperationFactProvider)host).ScanOperationFacts(
            query,
            fact =>
            {
                facts.Add(fact);
                return true;
            });
        return (facts.ToArray(), receipt);
    }

    private static RoslynCompilationHost HostWith(string source)
        => HostWithSources(("Engine.cs", source));

    private static RoslynCompilationHost HostWithSources(params (string Path, string Source)[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source.Source, path: source.Path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Test",
            trees,
            BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            {
                ["Test"] = compilation,
            },
            retainedProfileName: "Editor",
            availableProfiles: new[] { "Editor", "Player" });
    }

    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var references = new List<MetadataReference>();
        if (runtimeDir != null)
        {
            foreach (var name in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
            {
                var path = Path.Combine(runtimeDir, name);
                if (File.Exists(path)) references.Add(MetadataReference.CreateFromFile(path));
            }
        }
        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return references.ToArray();
    }
}
