using System.Text;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-WIRE-AUDIT-001 —
/// <see cref="ICompilationHost.GetWireAudit"/> flags members that are
/// referenced but structurally unplugged: private/internal fields read with
/// zero writes, and delegate slots never assigned. Read/write classification is
/// operation-tree based (assignment target / ++/-- / ref-out / initializer =
/// write). Neutral fixtures (<c>Acme</c>).
/// </summary>
public class WireAuditExtractorTests
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

    private static RoslynCompilationHost HostWith(string source, string moduleName = "Test")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            moduleName, new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [moduleName] = compilation,
        });
    }

    private const string Reader = @"
namespace Acme;
public class Reader
{
    private int _neverWritten;      // read, never assigned -> FieldReadWithoutWrite
    private int _writtenInCtor;     // assigned in ctor -> not flagged
    private int _initialized = 5;   // initializer -> not flagged
    public System.Action OnTick;    // delegate, never assigned -> DelegateSlotNeverAssigned
    public System.Action OnWired;   // delegate, assigned in ctor -> not flagged
    public Reader() { _writtenInCtor = 1; OnWired = () => {}; }
    public int Read() => _neverWritten + _writtenInCtor + _initialized;
}";

    private static WireAuditOptions Default => new();

    private static bool Has(WireAuditReport r, string kind, string memberName)
        => r.Findings.Any(f => f.Kind == kind && f.MemberName == memberName);

    [Fact]
    public void WireAudit_PrivateFieldReadNeverWritten_FlaggedReadWithoutWrite()
    {
        var report = HostWith(Reader).GetWireAudit(Default);
        Assert.True(Has(report, WireAuditFindingKind.FieldReadWithoutWrite, "_neverWritten"),
            "field read with zero writes must be flagged");
    }

    [Fact]
    public void WireAudit_FieldAssignedInCtorOrInitializer_NotFlagged()
    {
        var report = HostWith(Reader).GetWireAudit(Default);
        Assert.False(Has(report, WireAuditFindingKind.FieldReadWithoutWrite, "_writtenInCtor"));
        Assert.False(Has(report, WireAuditFindingKind.FieldReadWithoutWrite, "_initialized"));
    }

    [Fact]
    public void WireAudit_DelegateSlotNeverAssigned_Flagged()
    {
        var report = HostWith(Reader).GetWireAudit(Default);
        Assert.True(Has(report, WireAuditFindingKind.DelegateSlotNeverAssigned, "OnTick"),
            "delegate slot with zero assignment sites must be flagged");
        Assert.False(Has(report, WireAuditFindingKind.DelegateSlotNeverAssigned, "OnWired"),
            "ctor-assigned delegate must NOT be flagged");
    }

    [Fact]
    public void WireAudit_ObjectInitializerAssignment_CountsAsWrite()
    {
        var source = @"
namespace Acme;
public sealed class Bindings { public System.Action Slot; }
public class Builder { public void B() { var x = new Acme.Bindings { Slot = () => {} }; System.Console.WriteLine(x); } }";
        var report = HostWith(source).GetWireAudit(Default);
        Assert.False(Has(report, WireAuditFindingKind.DelegateSlotNeverAssigned, "Slot"),
            "object-initializer assignment must count as a write");
    }

    [Fact]
    public void WireAudit_PassesCanBeDisabledIndependently()
    {
        var fieldsOnly = HostWith(Reader).GetWireAudit(new WireAuditOptions { IncludeDelegateSlots = false });
        Assert.DoesNotContain(fieldsOnly.Findings, f => f.Kind == WireAuditFindingKind.DelegateSlotNeverAssigned);
        Assert.Contains(fieldsOnly.Findings, f => f.Kind == WireAuditFindingKind.FieldReadWithoutWrite);

        var delegatesOnly = HostWith(Reader).GetWireAudit(new WireAuditOptions { IncludeFieldReadWithoutWrite = false });
        Assert.DoesNotContain(delegatesOnly.Findings, f => f.Kind == WireAuditFindingKind.FieldReadWithoutWrite);
        Assert.Contains(delegatesOnly.Findings, f => f.Kind == WireAuditFindingKind.DelegateSlotNeverAssigned);
    }

    [Fact]
    public void WireAudit_TypeIdScope_RestrictsFindingsButNotCounting()
    {
        var report = HostWith(Reader).GetWireAudit(new WireAuditOptions { TypeId = "type:Acme.Reader" });
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f => Assert.Equal("type:Acme.Reader", f.DeclaringTypeId));

        var none = HostWith(Reader).GetWireAudit(new WireAuditOptions { TypeId = "type:Acme.DoesNotExist" });
        Assert.Empty(none.Findings);
    }

    // 30 private fields each read once, never written -> 30 FieldReadWithoutWrite.
    private static string ManyDeadReads(int n)
    {
        var sb = new StringBuilder("namespace Acme;\npublic class Many\n{\n");
        for (var i = 0; i < n; i++) sb.Append($"    private int _f{i};\n");
        sb.Append("    public int Read() { var r = 0;\n");
        for (var i = 0; i < n; i++) sb.Append($"        r += _f{i};\n");
        sb.Append("        return r; }\n}");
        return sb.ToString();
    }

    [Fact]
    public void WireAudit_SummarizeTrue_ForcesCompactCap_RegardlessOfCallerMaxFindings()
    {
        var report = HostWith(ManyDeadReads(30)).GetWireAudit(new WireAuditOptions { Summarize = true, MaxFindings = 100 });
        Assert.Equal(30, report.FindingCount);                // breakdown counts every finding
        Assert.True(report.Findings.Length <= RoslynWireAuditExtractor.SummarizeMaxFindings);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void WireAudit_SummarizeFalse_HonorsCallerMaxFindings()
    {
        var report = HostWith(ManyDeadReads(30)).GetWireAudit(new WireAuditOptions { MaxFindings = 30 });
        Assert.Equal(30, report.Findings.Length);             // summarize must not be sticky
        Assert.False(report.Truncated);
    }
}
