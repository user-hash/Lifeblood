using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-ASSIGNMENT-COVERAGE-001..004 —
/// <see cref="ICompilationHost.GetAssignmentCoverage"/> walks each
/// construction site of a target type and classifies per-slot
/// assignment status from <c>SemanticModel.GetOperation</c>. Every
/// fixture uses neutral vocabulary (<c>Acme.Bindings</c>,
/// <c>Owner</c>) and exercises one construction shape per fact.
/// </summary>
public class AssignmentCoverageExtractorTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir == null) return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var refs = new List<MetadataReference>();
        foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static RoslynCompilationHost HostWith(string source, string fileName = "Test.cs", string moduleName = "Test")
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

    private const string BindingsType = @"
namespace Acme;

public sealed class Bindings
{
    public System.Func<int, int> Calc;
    public System.Action<string> Notify;
    public System.Action Reset;
}
";

    private static AssignmentCoverageOptions Default => new();

    [Fact]
    public void AssignmentCoverage_InlineInitializerOnly_AllSlotsAssigned()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings
        {
            Calc = x => x + 1,
            Notify = msg => {},
            Reset = () => {}
        };
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        Assert.NotNull(report);
        Assert.Single(report!.Sites);
        var site = report.Sites[0];
        Assert.Equal(AssignmentCoverageConfidence.Proven, site.Confidence);
        Assert.All(site.Slots, s => Assert.Equal(AssignmentCoverageStatus.Assigned, s.Status));
    }

    [Fact]
    public void AssignmentCoverage_StatementAssignmentOnly_AllSlotsAssigned()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings();
        b.Calc = x => x + 1;
        b.Notify = msg => {};
        b.Reset = () => {};
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        Assert.NotNull(report);
        Assert.Single(report!.Sites);
        var site = report.Sites[0];
        Assert.Equal(AssignmentCoverageConfidence.Proven, site.Confidence);
        Assert.All(site.Slots, s => Assert.Equal(AssignmentCoverageStatus.Assigned, s.Status));
    }

    [Fact]
    public void AssignmentCoverage_MixedInlineAndStatement_BothCounted()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings { Calc = x => x + 1 };
        b.Notify = msg => {};
        b.Reset = () => {};
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        Assert.NotNull(report);
        var site = report!.Sites[0];
        Assert.Equal(AssignmentCoverageConfidence.Proven, site.Confidence);
        Assert.All(site.Slots, s => Assert.Equal(AssignmentCoverageStatus.Assigned, s.Status));
    }

    [Fact]
    public void AssignmentCoverage_PartialAssignment_AbsentSlotsListed()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings();
        b.Calc = x => x + 1;
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        Assert.NotNull(report);
        var site = report!.Sites[0];
        Assert.Equal(AssignmentCoverageStatus.Assigned, site.Slots.Single(s => s.SlotName == "Calc").Status);
        Assert.Equal(AssignmentCoverageStatus.Absent, site.Slots.Single(s => s.SlotName == "Notify").Status);
        Assert.Equal(AssignmentCoverageStatus.Absent, site.Slots.Single(s => s.SlotName == "Reset").Status);
    }

    [Fact]
    public void AssignmentCoverage_LambdaSlot_KindLambda()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings { Calc = x => x + 1 };
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var calc = report!.Sites[0].Slots.Single(s => s.SlotName == "Calc");
        Assert.Equal(AssignmentExpressionKind.Lambda, calc.ExpressionKind);
    }

    [Fact]
    public void AssignmentCoverage_MethodGroupSlot_KindMethodGroup()
    {
        var source = BindingsType + @"
public class Owner
{
    private int Add(int x) => x + 1;
    public void Build()
    {
        var b = new Acme.Bindings { Calc = Add };
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var calc = report!.Sites[0].Slots.Single(s => s.SlotName == "Calc");
        Assert.Equal(AssignmentExpressionKind.MethodGroup, calc.ExpressionKind);
    }

    [Fact]
    public void AssignmentCoverage_FieldReferenceSlot_KindFieldReference()
    {
        var source = BindingsType + @"
public class Owner
{
    private System.Func<int, int> _stored = x => x + 1;
    public void Build()
    {
        var b = new Acme.Bindings { Calc = _stored };
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var calc = report!.Sites[0].Slots.Single(s => s.SlotName == "Calc");
        Assert.Equal(AssignmentExpressionKind.FieldReference, calc.ExpressionKind);
    }

    [Fact]
    public void AssignmentCoverage_NullLiteralSlot_KindNullLiteral_StatusAssignedNull()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings { Calc = null };
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var calc = report!.Sites[0].Slots.Single(s => s.SlotName == "Calc");
        Assert.Equal(AssignmentCoverageStatus.AssignedNull, calc.Status);
        Assert.Equal(AssignmentExpressionKind.NullLiteral, calc.ExpressionKind);
    }

    [Fact]
    public void AssignmentCoverage_AssignmentAfterEscape_NotCounted()
    {
        var source = BindingsType + @"
public class Owner
{
    private void Configure(Acme.Bindings b) {}
    public void Build()
    {
        var b = new Acme.Bindings();
        Configure(b);
        b.Calc = x => x + 1;
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var site = report!.Sites[0];
        Assert.Contains(AssignmentCoverageSiteLimitation.PostEscapeAssignment, site.SiteLimitations);
        Assert.Equal(AssignmentCoverageConfidence.Advisory, site.Confidence);
        Assert.Equal(AssignmentCoverageStatus.Absent, site.Slots.Single(s => s.SlotName == "Calc").Status);
    }

    [Fact]
    public void AssignmentCoverage_BranchedAssignment_ConservativeAbsent()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build(bool cond)
    {
        var b = new Acme.Bindings();
        if (cond)
        {
            b.Calc = x => x + 1;
        }
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var site = report!.Sites[0];
        Assert.Contains(AssignmentCoverageSiteLimitation.BranchedMayAssign, site.SiteLimitations);
        Assert.Equal(AssignmentCoverageConfidence.Advisory, site.Confidence);
        Assert.Equal(AssignmentCoverageStatus.Absent, site.Slots.Single(s => s.SlotName == "Calc").Status);
    }

    [Fact]
    public void AssignmentCoverage_AliasedLocal_AdvisoryWithLimitation()
    {
        var source = BindingsType + @"
public class Owner
{
    public void Build()
    {
        var b = new Acme.Bindings();
        var b2 = b;
        b2.Calc = x => x + 1;
    }
}";
        var report = HostWith(source).GetAssignmentCoverage("type:Acme.Bindings", Default);
        var site = report!.Sites[0];
        Assert.Contains(AssignmentCoverageSiteLimitation.AliasedLocal, site.SiteLimitations);
        Assert.Equal(AssignmentCoverageConfidence.Advisory, site.Confidence);
    }
}
