using System.Text;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-FEATURE-SWITCH-001 —
/// <see cref="ICompilationHost.GetFeatureSwitchAudit"/> audits boolean fields /
/// settable boolean properties that gate branches and decides whether any
/// reachable write flips them off their default. The verdict turns on activation
/// reachability: a flag whose only flipping mutator has zero in-graph callers
/// reads as <c>AlwaysDefaultInGraph</c> (the dormant-feature shape). Neutral
/// fixtures (<c>Acme</c>).
/// </summary>
public class FeatureSwitchExtractorTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference>();
        if (runtimeDir != null)
            foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
            {
                var path = Path.Combine(runtimeDir, dll);
                if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
            }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static RoslynCompilationHost HostWith(string source, string fileName = "Engine.cs", string moduleName = "Test")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var compilation = CSharpCompilation.Create(
            moduleName, new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [moduleName] = compilation,
        });
    }

    private static FeatureSwitchAuditOptions Default => new();

    private static FeatureSwitch? Find(FeatureSwitchReport r, string name)
        => r.Switches.FirstOrDefault(s => s.MemberName == name);

    // The DAWG motivating shape: UseGrammarGeneration defaults false, read by a
    // live branch, flipped only inside SetGrammarMode — which nothing calls.
    private const string DormantFeature = @"
namespace Acme;
public class Engine
{
    private bool _useGrammar;                     // defaults false, gates a branch
    public void SetGrammarMode(bool on) { _useGrammar = on; }   // mutator, ZERO callers
    public int Generate() { if (_useGrammar) return 1; return 0; }
}";

    [Fact]
    public void FeatureSwitch_FlippedOnlyByUncalledMutator_AlwaysDefaultInGraph()
    {
        var report = HostWith(DormantFeature).GetFeatureSwitchAudit(Default);
        var sw = Find(report, "_useGrammar");
        Assert.NotNull(sw);
        Assert.Equal(FeatureSwitchVerdict.AlwaysDefaultInGraph, sw!.Verdict);
        Assert.Equal("false", sw.DefaultValue);
        Assert.True(sw.BranchConditionReadCount >= 1, "must be detected as branch-gating");
    }

    [Fact]
    public void FeatureSwitch_DormantMutator_ReportedWithZeroCallers()
    {
        var report = HostWith(DormantFeature).GetFeatureSwitchAudit(Default);
        var sw = Find(report, "_useGrammar")!;
        var mutator = Assert.Single(sw.Mutators);
        Assert.Equal("SetGrammarMode", mutator.MemberName);
        Assert.Equal(0, mutator.CallerCount);
        Assert.False(Assert.Single(sw.Assignments).Active, "write in an uncalled method is not active");
    }

    [Fact]
    public void FeatureSwitch_ProductionMutatorWithCaller_RuntimeMutable()
    {
        var source = @"
namespace Acme;
public class Engine
{
    private bool _useGrammar;
    public void SetGrammarMode(bool on) { _useGrammar = on; }
    public int Generate() { if (_useGrammar) return 1; return 0; }
}
public class Bootstrap { public void Init(Acme.Engine e) { e.SetGrammarMode(true); } }";
        var report = HostWith(source).GetFeatureSwitchAudit(Default);
        var sw = Find(report, "_useGrammar")!;
        Assert.Equal(FeatureSwitchVerdict.RuntimeMutable, sw.Verdict);
        Assert.True(Assert.Single(sw.Assignments).Active);
        Assert.Equal(1, Assert.Single(sw.Mutators).CallerCount);
    }

    [Fact]
    public void FeatureSwitch_FlippedOnlyFromTestCode_TestOnlyActivation()
    {
        // Production declares + branch-reads; the only flipping write is a direct
        // assignment in a *Tests.cs file (Test bucket, treated as an entry point).
        var prod = CSharpSyntaxTree.ParseText(@"
namespace Acme;
public class Engine
{
    public bool DebugMode;
    public int Generate() { if (DebugMode) return 1; return 0; }
}", path: "Engine.cs");
        var test = CSharpSyntaxTree.ParseText(@"
namespace Acme.Tests;
public class EngineTests { public void T() { var e = new Acme.Engine(); e.DebugMode = true; } }",
            path: "EngineTests.cs");
        var compilation = CSharpCompilation.Create("Test", new[] { prod, test }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal) { ["Test"] = compilation });

        var sw = Find(host.GetFeatureSwitchAudit(Default), "DebugMode")!;
        Assert.Equal(FeatureSwitchVerdict.TestOnlyActivation, sw.Verdict);
        Assert.Equal("Test", Assert.Single(sw.Assignments).Bucket);
    }

    [Fact]
    public void FeatureSwitch_SameAsDefaultConstantWrite_DoesNotFlip()
    {
        var source = @"
namespace Acme;
public class Engine
{
    private bool _flag;
    public void Reset() { _flag = false; }      // assigns the default -> no flip
    public int Use() { if (_flag) return 1; return 0; }
}
public class Boot { public void I(Acme.Engine e) { e.Reset(); } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "_flag")!;
        Assert.Equal(FeatureSwitchVerdict.AlwaysDefaultInGraph, sw.Verdict);
        Assert.False(Assert.Single(sw.Assignments).FlipsDefault);
        Assert.Empty(sw.Mutators);
    }

    [Fact]
    public void FeatureSwitch_RequireBranchCondition_FiltersNonGatingBooleans()
    {
        var source = @"
namespace Acme;
public class Bag
{
    private bool _gates;
    private bool _neverBranched;
    public int Use(System.Action<bool> sink) { sink(_neverBranched); if (_gates) return 1; return 0; }
}";
        var gated = HostWith(source).GetFeatureSwitchAudit(Default);
        Assert.NotNull(Find(gated, "_gates"));
        Assert.Null(Find(gated, "_neverBranched"));

        var all = HostWith(source).GetFeatureSwitchAudit(new FeatureSwitchAuditOptions { RequireBranchCondition = false });
        Assert.NotNull(Find(all, "_neverBranched"));
    }

    [Fact]
    public void FeatureSwitch_NotLogicAndStaticProperty_StillDetectedAsGating()
    {
        var source = @"
namespace Acme;
public static class Flags
{
    public static bool Verbose { get; set; }
    public static int Log() { if (!Verbose) return 0; return 1; }
}";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "Verbose")!;
        Assert.Equal("Property", sw.MemberKind);
        Assert.True(sw.IsStatic);
        Assert.True(sw.BranchConditionReadCount >= 1, "negated read must count as branch-gating");
    }

    [Fact]
    public void FeatureSwitch_TypeIdScope_RestrictsFindings()
    {
        var report = HostWith(DormantFeature).GetFeatureSwitchAudit(new FeatureSwitchAuditOptions { TypeId = "type:Acme.Engine" });
        Assert.All(report.Switches, s => Assert.Equal("type:Acme.Engine", s.DeclaringTypeId));

        var none = HostWith(DormantFeature).GetFeatureSwitchAudit(new FeatureSwitchAuditOptions { TypeId = "type:Acme.DoesNotExist" });
        Assert.Empty(none.Switches);
    }

    [Fact]
    public void FeatureSwitch_MutatorCalledThroughInterface_CountsAsReachable()
    {
        // The flipping write lives in a concrete IDisposable-style impl, but every
        // caller invokes it through the interface — IInvocationOperation.TargetMethod
        // binds to the interface member, not the concrete. Dispatch aliasing must
        // credit the concrete so this is RuntimeMutable, not a false dormant.
        var source = @"
namespace Acme;
public interface ICapture { void Stop(); }
public sealed class Capture : ICapture
{
    private bool _stopped;
    public void Stop() { _stopped = true; }
    public int Use() { if (_stopped) return 1; return 0; }
}
public class Boot { public void I(Acme.ICapture c) { c.Stop(); } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "_stopped")!;
        Assert.Equal(FeatureSwitchVerdict.RuntimeMutable, sw.Verdict);
        Assert.True(Assert.Single(sw.Assignments).Active, "interface-dispatched mutator must read as reachable");
        Assert.Equal(1, Assert.Single(sw.Mutators).CallerCount);
    }

    [Fact]
    public void FeatureSwitch_DisposeGuardReachedViaUsing_CountsAsReachable()
    {
        // _disposed is flipped only in Dispose(); the sole caller is a `using`
        // statement, which synthesizes IDisposable.Dispose (not an invocation).
        var source = @"
namespace Acme;
public sealed class Res : System.IDisposable
{
    private bool _disposed;
    public void Dispose() { _disposed = true; }
    public int Work() { if (_disposed) return 1; return 0; }
}
public class Boot { public void I() { using (var r = new Acme.Res()) { r.Work(); } } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "_disposed")!;
        Assert.Equal(FeatureSwitchVerdict.RuntimeMutable, sw.Verdict);
        Assert.True(Assert.Single(sw.Assignments).Active, "using-disposed mutator must read as reachable");
    }

    [Fact]
    public void FeatureSwitch_DefaultTrueInitializer_FlipToFalseIsFlippingWrite()
    {
        var source = @"
namespace Acme;
public class Engine
{
    private bool _enabled = true;               // default true
    public void Disable() { _enabled = false; } // flips off default
    public int Use() { if (_enabled) return 1; return 0; }
}
public class Boot { public void I(Acme.Engine e) { e.Disable(); } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "_enabled")!;
        Assert.Equal("true", sw.DefaultValue);
        Assert.True(Assert.Single(sw.Assignments).FlipsDefault);
        Assert.Equal(FeatureSwitchVerdict.RuntimeMutable, sw.Verdict);
    }

    // 30 distinct branch-gating booleans on one type.
    private static string ManyGatedBooleans(int n)
    {
        var sb = new StringBuilder("namespace Acme;\npublic class Many\n{\n");
        for (var i = 0; i < n; i++) sb.Append($"    private bool _f{i};\n");
        sb.Append("    public int Use() { var r = 0;\n");
        for (var i = 0; i < n; i++) sb.Append($"        if (_f{i}) r++;\n");
        sb.Append("        return r; }\n}");
        return sb.ToString();
    }

    [Fact]
    public void FeatureSwitch_SummarizeTrue_ForcesCompactCensus_RegardlessOfCallerMaxFindings()
    {
        var report = HostWith(ManyGatedBooleans(30))
            .GetFeatureSwitchAudit(new FeatureSwitchAuditOptions { Summarize = true, MaxFindings = 100 });
        Assert.Equal(30, report.SwitchCount);                 // breakdown counts every switch
        Assert.True(report.Switches.Length <= RoslynFeatureSwitchExtractor.SummarizeMaxFindings);
        Assert.True(report.Truncated);
        // Census shape: the per-switch evidence arrays are dropped (a count cap
        // alone would not bound size — a widely-read flag carries many gates).
        Assert.All(report.Switches, s =>
        {
            Assert.Empty(s.BranchGatedMembers);
            Assert.Empty(s.Assignments);
            Assert.Empty(s.Mutators);
        });
    }

    [Fact]
    public void FeatureSwitch_NotSummarized_KeepsEvidenceArrays()
    {
        var report = HostWith(ManyGatedBooleans(5)).GetFeatureSwitchAudit(Default);
        Assert.All(report.Switches, s => Assert.NotEmpty(s.BranchGatedMembers));
    }

    [Fact]
    public void FeatureSwitch_SummarizeFalse_HonorsCallerMaxFindings()
    {
        var report = HostWith(ManyGatedBooleans(30))
            .GetFeatureSwitchAudit(new FeatureSwitchAuditOptions { MaxFindings = 30 });
        Assert.Equal(30, report.Switches.Length);             // summarize must not be sticky
        Assert.False(report.Truncated);
    }

    [Fact]
    public void FeatureSwitch_PositionalRecord_DefaultFromParameter_AndConstructorArgIsWrite()
    {
        // The DAWG-shape false positive: a positional-record option's default lives
        // on the parameter (= true), and its writes come through CONSTRUCTOR ARGS
        // (new Options(false)), not property assignments — both were invisible.
        var source = @"
namespace Acme;
public sealed record Options(bool ExcludePublic = true)
{
    public int Use() { if (ExcludePublic) return 1; return 0; }
}
public class Boot { public bool Sink; public Boot() { var o = new Acme.Options(false); Sink = o.Use() > 0; } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "ExcludePublic")!;
        Assert.Equal("true", sw.DefaultValue);                        // read off the record parameter
        Assert.Equal(FeatureSwitchVerdict.RuntimeMutable, sw.Verdict); // constructor-arg write detected
        Assert.Contains(sw.Assignments, a => a.AssignedValue == "false" && a.FlipsDefault && a.Active);
    }

    [Fact]
    public void FeatureSwitch_PositionalRecord_OmittedArg_IsNotAWrite()
    {
        // Omitting the arg uses the parameter default — not a write, so a record
        // option nothing ever sets stays correctly AlwaysDefaultInGraph.
        var source = @"
namespace Acme;
public sealed record Options(bool Flag = false)
{
    public int Use() { if (Flag) return 1; return 0; }
}
public class Boot { public bool Sink; public Boot() { var o = new Acme.Options(); Sink = o.Use() > 0; } }";
        var sw = Find(HostWith(source).GetFeatureSwitchAudit(Default), "Flag")!;
        Assert.Equal("false", sw.DefaultValue);
        Assert.Equal(FeatureSwitchVerdict.AlwaysDefaultInGraph, sw.Verdict);
        Assert.Empty(sw.Assignments);
    }
}
