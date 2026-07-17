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
    public void Scan_ComparisonPredicateCarriesExactOperandProvenanceAndSource()
    {
        const string source = """
            namespace Acme;
            public sealed class Boundary
            {
                private void Sink(int value) { }
                public void Run(int frames)
                {
                    for (var i = 0; i <= frames - 1; i++)
                        Sink(i);
                }
            }
            """;
        using var host = HostWithSources(("Boundary.cs", source));
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            TargetSymbolIds = new[] { "method:Acme.Boundary.Sink(int)" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        var context = Assert.Single(Assert.Single(facts).ControlContexts);
        var predicate = Assert.Single(
            context.Predicates,
            candidate => candidate.Operator == "LessThanOrEqual");

        Assert.Equal("LessThanOrEqual", predicate.Operator);
        Assert.EndsWith("Boundary.cs", predicate.Source.FilePath, StringComparison.Ordinal);
        Assert.Equal(7, predicate.Source.Line);
        Assert.Contains(predicate.LeftValue!.SourceSymbolIds, id => id.EndsWith(":i", StringComparison.Ordinal));
        Assert.Equal(OperationValueKind.Binary, predicate.RightValue!.Kind);
        Assert.Contains(
            "parameter:method:Acme.Boundary.Run(int)#0:frames",
            predicate.RightValue.SourceSymbolIds);
        Assert.Equal(new[] { "Subtract" }, predicate.RightValue.Operators);
        Assert.Equal("1", Assert.Single(predicate.RightValue.Constants).Value);
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
    public void Scan_BranchFactsAndNestedOccurrencesPreserveExactArmPlacement()
    {
        const string source = """
            namespace Acme;
            public sealed class Voice
            {
                private float _tail;

                public float Step(float level, float sample)
                {
                    if (level <= 0.001f)
                        _tail = 0f;
                    else
                        _tail = sample;

                    return level > 0.5f ? 0f : sample;
                }
            }
            """;
        using var host = HostWithSources(("Voice.cs", source));
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            IncludeKinds = new[] { OperationFactKind.Assignment, OperationFactKind.Branch },
        });

        var resets = facts
            .Where(fact => fact.TargetSymbolId == "field:Acme.Voice._tail")
            .OrderBy(fact => fact.Source.Line)
            .ToArray();
        Assert.Equal(2, resets.Length);
        Assert.Equal(
            OperationBranchArm.WhenTrue,
            Assert.Single(resets[0].ControlContexts, context =>
                context.Kind == OperationControlContextKind.Branch).BranchArm);
        Assert.Equal(
            OperationBranchArm.WhenFalse,
            Assert.Single(resets[1].ControlContexts, context =>
                context.Kind == OperationControlContextKind.Branch).BranchArm);

        var statementBranch = Assert.Single(facts, fact =>
            fact.Kind == OperationFactKind.Branch
            && fact.Inputs.Any(input =>
                input.Role == OperationInputRole.Condition
                && input.Value.Expression == "level <= 0.001f"));
        Assert.Equal(
            new[] { OperationInputRole.Condition },
            statementBranch.Inputs.Select(input => input.Role));

        var conditional = Assert.Single(facts, fact =>
            fact.Kind == OperationFactKind.Branch
            && fact.Inputs.Any(input =>
                input.Role == OperationInputRole.Condition
                && input.Value.Expression == "level > 0.5f"));
        var whenTrue = Assert.Single(conditional.Inputs, input => input.Role == OperationInputRole.WhenTrue);
        var whenFalse = Assert.Single(conditional.Inputs, input => input.Role == OperationInputRole.WhenFalse);
        Assert.True(whenTrue.Value.IsCompileTimeConstant);
        Assert.Equal("0", Assert.Single(whenTrue.Value.Constants).Value);
        Assert.Contains(
            "parameter:method:Acme.Voice.Step(float,float)#1:sample",
            whenFalse.Value.SourceSymbolIds);
    }

    [Fact]
    public void Scan_ValueCarriesNestedOperatorsWithoutRetainingCompilerObjects()
    {
        const string source = """
            namespace Acme;
            public sealed class Clock
            {
                private void SetSeconds(float seconds) { }
                public void Run(float frames, float sampleRate) => SetSeconds(frames / sampleRate);
            }
            """;
        using var host = HostWith(source);
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            TargetSymbolIds = new[] { "method:Acme.Clock.SetSeconds(float)" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        var argument = Assert.Single(
            Assert.Single(facts).Inputs,
            input => input.Role == OperationInputRole.Argument).Value;

        Assert.Equal(new[] { "Divide" }, argument.Operators);
        Assert.Contains("parameter:method:Acme.Clock.Run(float,float)#0:frames", argument.SourceSymbolIds);
        Assert.Contains("parameter:method:Acme.Clock.Run(float,float)#1:sampleRate", argument.SourceSymbolIds);
    }

    [Fact]
    public void Scan_ValueCarriesLiteralNamedAndNonFiniteConstantProvenance()
    {
        const string source = """
            namespace Acme;
            public sealed class Constants
            {
                private const float PolicyFloor = 0.0001f;
                private void Sink(float value) { }
                public void Run(float input)
                {
                    Sink(input * 0.00011f);
                    Sink(PolicyFloor);
                    Sink(float.PositiveInfinity);
                }
            }
            """;
        using var host = HostWith(source);
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            TargetSymbolIds = new[] { "method:Acme.Constants.Sink(float)" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        Assert.Equal(3, facts.Length);
        var raw = Assert.Single(facts, fact => ArgumentValue(fact).Expression == "input * 0.00011f");
        var rawConstant = Assert.Single(ArgumentValue(raw).Constants);
        Assert.Equal(OperationConstantOrigin.Literal, rawConstant.Origin);
        Assert.Equal("0.00011", rawConstant.Value);
        Assert.Equal(OperationNumericClassification.Finite, rawConstant.NumericClassification);

        var named = Assert.Single(facts, fact => ArgumentValue(fact).Expression == "PolicyFloor");
        var namedConstant = Assert.Single(ArgumentValue(named).Constants);
        Assert.Equal(OperationConstantOrigin.NamedConstant, namedConstant.Origin);
        Assert.Equal("field:Acme.Constants.PolicyFloor", namedConstant.SymbolId);
        Assert.Equal(OperationNumericClassification.Finite, namedConstant.NumericClassification);

        var infinity = Assert.Single(facts, fact => ArgumentValue(fact).Expression == "float.PositiveInfinity");
        var infinityConstant = Assert.Single(ArgumentValue(infinity).Constants);
        Assert.Equal(OperationConstantOrigin.NamedConstant, infinityConstant.Origin);
        Assert.Equal(OperationNumericClassification.PositiveInfinity, infinityConstant.NumericClassification);
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

    [Fact]
    public void Scan_TargetPrefilterPreservesUnfilteredFactIdentityAndProjection()
    {
        using var host = HostWith(Source);
        var (allCalls, _) = Scan(host, new OperationFactQuery
        {
            IncludeKinds = new[] { OperationFactKind.Call },
        });
        var (filteredCalls, _) = Scan(host, new OperationFactQuery
        {
            TargetSymbolIds = new[] { "method:Acme.Engine.Sink(float)" },
            IncludeKinds = new[] { OperationFactKind.Call },
        });

        var expected = allCalls
            .Where(fact => fact.TargetSymbolId == "method:Acme.Engine.Sink(float)")
            .ToArray();

        Assert.Equal(expected.Select(fact => fact.Id), filteredCalls.Select(fact => fact.Id));
        Assert.Equal(expected.Select(fact => fact.Source.Line), filteredCalls.Select(fact => fact.Source.Line));
        Assert.Equal(
            expected.Select(fact => Assert.Single(
                fact.Inputs,
                input => input.Role == OperationInputRole.Argument).Value.Expression),
            filteredCalls.Select(fact => Assert.Single(
                fact.Inputs,
                input => input.Role == OperationInputRole.Argument).Value.Expression));
    }

    [Fact]
    public void Scan_DisjunctiveSelectorsKeepTargetlessElementAndShiftFactsNarrow()
    {
        const string source = """
            namespace Acme;
            public sealed class Shapes
            {
                public ulong Run(int[] samples, int frameIndex, int maskBit)
                {
                    var sample = samples[frameIndex];
                    var narrow = 1 << maskBit;
                    var wide = 1UL << maskBit;
                    return (ulong)(sample + narrow) | wide;
                }
            }
            """;
        using var host = HostWithSources(("Shapes.cs", source));
        var containing = "method:Acme.Shapes.Run(int[],int,int)";
        var (facts, receipt) = Scan(host, new OperationFactQuery
        {
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.ElementAccess },
                    ContainingSymbolIds = new[] { containing },
                },
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.Binary },
                    ContainingSymbolIds = new[] { containing },
                    Operators = new[] { "LeftShift" },
                },
            },
        });

        Assert.Equal(3, facts.Length);
        var element = Assert.Single(facts, fact => fact.Kind == OperationFactKind.ElementAccess);
        Assert.Null(element.TargetSymbolId);
        Assert.Contains(element.Inputs, input =>
            input.Role == OperationInputRole.Index
            && input.Value.SourceSymbolIds.Contains(
                "parameter:method:Acme.Shapes.Run(int[],int,int)#1:frameIndex"));
        var shifts = facts.Where(fact => fact.Operator == "LeftShift").ToArray();
        Assert.Equal(new[] { "int", "ulong" }, shifts.Select(fact => fact.ResultType));
        Assert.Equal(3, receipt.EmittedFactCount);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
    }

    [Fact]
    public void Scan_MemberAndElementReferencesCarryExactReadWriteMode()
    {
        const string source = """
            namespace Acme;
            public static class State
            {
                private static readonly int[] Buffer = new int[4];
                private static int Value;
                public static int Run(int index)
                {
                    var read = Buffer[index];
                    Buffer[index] = read + 1;
                    Value++;
                    return Value;
                }
            }
            """;
        using var host = HostWithSources(("State.cs", source));
        var containing = "method:Acme.State.Run(int)";
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[]
                    {
                        OperationFactKind.MemberRead,
                        OperationFactKind.MemberWrite,
                        OperationFactKind.ElementAccess,
                    },
                    ContainingSymbolIds = new[] { containing },
                },
            },
        });

        Assert.Contains(facts, fact =>
            fact.Kind == OperationFactKind.ElementAccess && fact.Operator == OperationAccessMode.Read);
        Assert.Contains(facts, fact =>
            fact.Kind == OperationFactKind.ElementAccess && fact.Operator == OperationAccessMode.Write);
        Assert.Contains(facts, fact =>
            fact.TargetSymbolId == "field:Acme.State.Value"
            && fact.Kind == OperationFactKind.MemberWrite
            && fact.Operator == OperationAccessMode.Write);
        Assert.Contains(facts, fact =>
            fact.TargetSymbolId == "field:Acme.State.Value"
            && fact.Kind == OperationFactKind.MemberRead
            && fact.Operator == OperationAccessMode.Read);
    }

    [Fact]
    public void Scan_PointerIndexingCarriesReceiverAndIndexShape()
    {
        const string source = """
            namespace Acme;
            public static unsafe class PointerBuffer
            {
                public static float Read(float* samples, int index) => samples[index];
                public static float ReadFirst(float* samples) => *samples;
            }
            """;
        using var host = HostWithSources(("PointerBuffer.cs", source));
        var containing = "method:Acme.PointerBuffer.Read(float*,int)";
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.ElementAccess },
                    ContainingSymbolIds = new[] { containing },
                },
            },
        });

        var element = Assert.Single(facts);
        var receiver = Assert.Single(element.Inputs, input => input.Role == OperationInputRole.Receiver);
        var index = Assert.Single(element.Inputs, input => input.Role == OperationInputRole.Index);
        Assert.Equal("float*", receiver.Value.Type);
        Assert.Contains("parameter:method:Acme.PointerBuffer.Read(float*,int)#0:samples", receiver.Value.SourceSymbolIds);
        Assert.Contains("parameter:method:Acme.PointerBuffer.Read(float*,int)#1:index", index.Value.SourceSymbolIds);

        var (directFacts, _) = Scan(host, new OperationFactQuery
        {
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.PointerIndirection },
                    ContainingSymbolIds = new[] { "method:Acme.PointerBuffer.ReadFirst(float*)" },
                },
            },
        });
        var direct = Assert.Single(directFacts);
        Assert.Single(direct.Inputs, input => input.Role == OperationInputRole.Receiver);
    }

    [Fact]
    public void Scan_EnumMaskInitializersCarryShiftWidthAndBitConstants()
    {
        const string source = """
            namespace Acme;
            public enum Bits : ulong
            {
                First = 1UL << 0,
                Duplicate = 1UL << 0,
                Last = 1UL << 63,
            }
            """;
        using var host = HostWithSources(("Bits.cs", source));
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            FilePaths = new[] { "Bits.cs" },
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.Binary },
                    Operators = new[] { "LeftShift" },
                },
            },
        });

        Assert.Equal(3, facts.Length);
        Assert.All(facts, fact => Assert.Equal("ulong", fact.ResultType));
        Assert.Equal(new[] { "0", "0", "63" }, facts.Select(fact => Assert.Single(
            fact.Inputs,
            input => input.Role == OperationInputRole.Right).Value.ConstantValue));
        Assert.All(facts, fact => Assert.StartsWith("field:Acme.Bits.", fact.ContainingSymbolId, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_IndexerContainersCarryReceiverAndBoundIndexShape()
    {
        const string source = """
            namespace Acme;
            public readonly struct Sidecar
            {
                public float this[int slot] => 0f;
            }
            public static class Indexers
            {
                public static float ReadSidecar(Sidecar values, int slot) => values[slot];
                public static float ReadSpan(System.Span<float> values, int frame) => values[frame];
            }
            """;
        using var host = HostWithSources(("Indexers.cs", source));
        var (facts, _) = Scan(host, new OperationFactQuery
        {
            Selectors = new[]
            {
                new OperationFactSelector
                {
                    IncludeKinds = new[] { OperationFactKind.ElementAccess },
                    ContainingSymbolIds = new[]
                    {
                        "method:Acme.Indexers.ReadSidecar(Acme.Sidecar,int)",
                        "method:Acme.Indexers.ReadSpan(System.Span<float>,int)",
                    },
                },
            },
        });

        Assert.Equal(2, facts.Length);
        Assert.All(facts, fact =>
        {
            Assert.NotNull(fact.TargetSymbolId);
            Assert.Single(fact.Inputs, input => input.Role == OperationInputRole.Receiver);
            var index = Assert.Single(fact.Inputs, input => input.Role == OperationInputRole.Index);
            Assert.Equal(0, index.Ordinal);
            Assert.Contains(index.Value.SourceSymbolIds, id =>
                id.EndsWith(":slot", StringComparison.Ordinal)
                || id.EndsWith(":frame", StringComparison.Ordinal));
        });
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

    private static OperationValueFact ArgumentValue(OperationFact fact)
        => Assert.Single(fact.Inputs, input => input.Role == OperationInputRole.Argument).Value;

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
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
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
