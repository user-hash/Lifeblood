using System.IO;
using System.Linq;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using SymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// F3a — IPortHealthAnalyzer seam tests. The algorithm was relocated from
/// the inline <c>HandlePortHealth</c> body in <c>ToolHandler.cs</c> behind
/// a stable Application-layer port. This atom adds the regression coverage
/// the pre-F3a inline implementation never had.
///
/// INV-PORT-HEALTH-ANALYZER-SEAM-001 / LB-TRACK-20260519-022.
/// </summary>
public class PortHealthAnalyzerTests
{
    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    private static IPortHealthAnalyzer Analyzer() => new LifebloodPortHealthAnalyzer();

    [Fact]
    public void Analyze_TypeWithAllLiveMembers_ReportsHealthy()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Port", Name = "Port", Kind = SymbolKind.Type, FilePath = "Port.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Port.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Port.B()", Name = "B", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.A()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.B()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.Port.A()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.Port.B()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Port");

        Assert.NotNull(report);
        Assert.Equal("type:N.Port", report!.TypeId);
        Assert.Equal(2, report.MemberCount);
        Assert.Equal(2, report.LiveMembers);
        Assert.Equal(0, report.DeadMembers);
        Assert.Equal(1.0, report.LivenessPct);
        Assert.Equal("healthy", report.Verdict);
    }

    [Fact]
    public void Analyze_TypeWithNoMembers_ReportsEmpty()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Marker", Name = "Marker", Kind = SymbolKind.Type, FilePath = "Marker.cs" })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Marker");

        Assert.NotNull(report);
        Assert.Equal(0, report!.MemberCount);
        Assert.Equal("empty", report.Verdict);
        Assert.Equal(0.0, report.LivenessPct);
    }

    [Fact]
    public void Analyze_TypeWithAllDeadMembers_ReportsVestigial()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Port", Name = "Port", Kind = SymbolKind.Type, FilePath = "Port.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Port.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Port.B()", Name = "B", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.A()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.B()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Port");

        Assert.NotNull(report);
        Assert.Equal(2, report!.MemberCount);
        Assert.Equal(0, report.LiveMembers);
        Assert.Equal(2, report.DeadMembers);
        Assert.Equal("vestigial", report.Verdict);
    }

    [Fact]
    public void Analyze_TypeWithMixedMembers_ReportsMixed()
    {
        // Three members, one live (33% liveness → "mixed" band).
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Port", Name = "Port", Kind = SymbolKind.Type, FilePath = "Port.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Port.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Port.B()", Name = "B", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Port.C()", Name = "C", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.A()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.B()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.C()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.Port.A()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Port");

        Assert.NotNull(report);
        Assert.Equal(3, report!.MemberCount);
        Assert.Equal(1, report.LiveMembers);
        Assert.Equal(2, report.DeadMembers);
        Assert.Equal("mixed", report.Verdict);
    }

    [Fact]
    public void Analyze_ImplementerMethodWithoutIncomingCalls_CountsAsLive()
    {
        // Method that implements an interface member is reachable through
        // the contract — outgoing Implements edge marks it live even with
        // zero incoming Calls. Mirrors the dead-code analyzer's same rule.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Impl", Name = "Impl", Kind = SymbolKind.Type, FilePath = "Impl.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Impl.Run()", Name = "Run", Kind = SymbolKind.Method, FilePath = "Impl.cs", ParentId = "type:N.Impl" })
            .AddSymbol(new Symbol { Id = "type:N.IRunnable", Name = "IRunnable", Kind = SymbolKind.Type, FilePath = "IRunnable.cs" })
            .AddSymbol(new Symbol { Id = "method:N.IRunnable.Run()", Name = "Run", Kind = SymbolKind.Method, FilePath = "IRunnable.cs", ParentId = "type:N.IRunnable" })
            .AddEdge(new Edge { SourceId = "type:N.Impl", TargetId = "method:N.Impl.Run()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Impl.Run()", TargetId = "method:N.IRunnable.Run()", Kind = EdgeKind.Implements, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Impl");

        Assert.NotNull(report);
        Assert.Equal(1, report!.LiveMembers);
        Assert.Equal("healthy", report.Verdict);
    }

    [Fact]
    public void Analyze_NestedTypesAreExcluded()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Outer", Name = "Outer", Kind = SymbolKind.Type, FilePath = "Outer.cs" })
            .AddSymbol(new Symbol { Id = "type:N.Outer.Inner", Name = "Inner", Kind = SymbolKind.Type, FilePath = "Outer.cs", ParentId = "type:N.Outer" })
            .AddSymbol(new Symbol { Id = "method:N.Outer.Run()", Name = "Run", Kind = SymbolKind.Method, FilePath = "Outer.cs", ParentId = "type:N.Outer" })
            .AddEdge(new Edge { SourceId = "type:N.Outer", TargetId = "type:N.Outer.Inner", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.Outer", TargetId = "method:N.Outer.Run()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Outer");

        Assert.NotNull(report);
        Assert.Equal(1, report!.MemberCount); // nested Inner type excluded
    }

    // ─────────────────────────────────────────────────────────────────────
    // F3b — composite / inherited-interface surface
    // (INV-PORT-HEALTH-COMPOSITE-001 / LB-TRACK-20260518-017).
    // Composite host ports commonly carry 0 direct members and reach all
    // their surface through inherited sub-ports; the analyzer must walk
    // outgoing Inherits edges, sum the aggregate surface, and compute the
    // verdict against the aggregate so composites no longer mislabel as
    // vestigial.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_NonCompositeInterface_ReportsZeroInheritedMembers()
    {
        // Backwards-compat: a type with no outgoing Inherits edges reports
        // DirectMemberCount == AggregateMemberCount == MemberCount, with
        // InheritedMemberCount == 0 and IsCompositeInterface == false.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Port", Name = "Port", Kind = SymbolKind.Type, FilePath = "Port.cs" })
            .AddSymbol(new Symbol { Id = "method:N.Port.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "Port.cs", ParentId = "type:N.Port" })
            .AddEdge(new Edge { SourceId = "type:N.Port", TargetId = "method:N.Port.A()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.Port");

        Assert.NotNull(report);
        Assert.False(report!.IsCompositeInterface);
        Assert.Empty(report.InheritedInterfaces);
        Assert.Equal(0, report.InheritedMemberCount);
        Assert.Equal(1, report.DirectMemberCount);
        Assert.Equal(1, report.AggregateMemberCount);
        Assert.Equal(1, report.MemberCount); // back-compat alias
    }

    [Fact]
    public void Analyze_CompositeInterfaceWithLiveInheritedMembers_ReportsHealthy()
    {
        // Composite host port with 0 direct, 2 inherited members both live.
        // Pre-F3b the verdict was "empty" because MemberCount was 0;
        // post-F3b the verdict is "healthy" against the aggregate surface.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = SymbolKind.Type, FilePath = "IComposite.cs" })
            .AddSymbol(new Symbol { Id = "type:N.ISubA", Name = "ISubA", Kind = SymbolKind.Type, FilePath = "ISubA.cs" })
            .AddSymbol(new Symbol { Id = "type:N.ISubB", Name = "ISubB", Kind = SymbolKind.Type, FilePath = "ISubB.cs" })
            .AddSymbol(new Symbol { Id = "method:N.ISubA.Run()", Name = "Run", Kind = SymbolKind.Method, FilePath = "ISubA.cs", ParentId = "type:N.ISubA" })
            .AddSymbol(new Symbol { Id = "method:N.ISubB.Tick()", Name = "Tick", Kind = SymbolKind.Method, FilePath = "ISubB.cs", ParentId = "type:N.ISubB" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISubA", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISubB", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISubA", TargetId = "method:N.ISubA.Run()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISubB", TargetId = "method:N.ISubB.Tick()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.ISubA.Run()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.ISubB.Tick()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.IComposite");

        Assert.NotNull(report);
        Assert.True(report!.IsCompositeInterface);
        Assert.Equal(new[] { "type:N.ISubA", "type:N.ISubB" }, report.InheritedInterfaces);
        Assert.Equal(0, report.DirectMemberCount);
        Assert.Equal(2, report.InheritedMemberCount);
        Assert.Equal(2, report.AggregateMemberCount);
        Assert.Equal(2, report.LiveMembers);
        Assert.Equal("healthy", report.Verdict);
    }

    [Fact]
    public void Analyze_CompositeInterfaceWithMixedInheritedMembers_ReportsMixed()
    {
        // 0 direct + 3 inherited (1 live, 2 dead) → 33% liveness → "mixed".
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = SymbolKind.Type, FilePath = "IComposite.cs" })
            .AddSymbol(new Symbol { Id = "type:N.ISubA", Name = "ISubA", Kind = SymbolKind.Type, FilePath = "ISubA.cs" })
            .AddSymbol(new Symbol { Id = "method:N.ISubA.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "ISubA.cs", ParentId = "type:N.ISubA" })
            .AddSymbol(new Symbol { Id = "method:N.ISubA.B()", Name = "B", Kind = SymbolKind.Method, FilePath = "ISubA.cs", ParentId = "type:N.ISubA" })
            .AddSymbol(new Symbol { Id = "method:N.ISubA.C()", Name = "C", Kind = SymbolKind.Method, FilePath = "ISubA.cs", ParentId = "type:N.ISubA" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISubA", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISubA", TargetId = "method:N.ISubA.A()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISubA", TargetId = "method:N.ISubA.B()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISubA", TargetId = "method:N.ISubA.C()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.ISubA.A()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.IComposite");

        Assert.NotNull(report);
        Assert.Equal(3, report!.AggregateMemberCount);
        Assert.Equal(1, report.LiveMembers);
        Assert.Equal(2, report.DeadMembers);
        Assert.Equal("mixed", report.Verdict);
    }

    [Fact]
    public void Analyze_TransitiveInheritance_WalksDistinctAcrossClosure()
    {
        // IComposite → IMiddle → IBase; all members from IBase + IMiddle count.
        // Diamond inheritance (two paths reach the same base) must not
        // double-count either the interface or its members.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = SymbolKind.Type, FilePath = "IComposite.cs" })
            .AddSymbol(new Symbol { Id = "type:N.IMiddleA", Name = "IMiddleA", Kind = SymbolKind.Type, FilePath = "IMiddleA.cs" })
            .AddSymbol(new Symbol { Id = "type:N.IMiddleB", Name = "IMiddleB", Kind = SymbolKind.Type, FilePath = "IMiddleB.cs" })
            .AddSymbol(new Symbol { Id = "type:N.IBase", Name = "IBase", Kind = SymbolKind.Type, FilePath = "IBase.cs" })
            .AddSymbol(new Symbol { Id = "method:N.IMiddleA.MidA()", Name = "MidA", Kind = SymbolKind.Method, FilePath = "IMiddleA.cs", ParentId = "type:N.IMiddleA" })
            .AddSymbol(new Symbol { Id = "method:N.IMiddleB.MidB()", Name = "MidB", Kind = SymbolKind.Method, FilePath = "IMiddleB.cs", ParentId = "type:N.IMiddleB" })
            .AddSymbol(new Symbol { Id = "method:N.IBase.Base()", Name = "Base", Kind = SymbolKind.Method, FilePath = "IBase.cs", ParentId = "type:N.IBase" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            // IComposite : IMiddleA, IMiddleB
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.IMiddleA", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.IMiddleB", Kind = EdgeKind.Inherits, Evidence = Evidence })
            // Both middles inherit from IBase — diamond.
            .AddEdge(new Edge { SourceId = "type:N.IMiddleA", TargetId = "type:N.IBase", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IMiddleB", TargetId = "type:N.IBase", Kind = EdgeKind.Inherits, Evidence = Evidence })
            // Contains
            .AddEdge(new Edge { SourceId = "type:N.IMiddleA", TargetId = "method:N.IMiddleA.MidA()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IMiddleB", TargetId = "method:N.IMiddleB.MidB()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IBase", TargetId = "method:N.IBase.Base()", Kind = EdgeKind.Contains, Evidence = Evidence })
            // Live edges into every member.
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.IMiddleA.MidA()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.IMiddleB.MidB()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.IBase.Base()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.IComposite");

        Assert.NotNull(report);
        Assert.Equal(3, report!.InheritedInterfaces.Length); // distinct: IMiddleA, IMiddleB, IBase
        Assert.Contains("type:N.IBase", report.InheritedInterfaces);
        Assert.Equal(3, report.InheritedMemberCount); // MidA + MidB + Base (Base counted once across diamond)
        Assert.Equal("healthy", report.Verdict);
    }

    [Fact]
    public void Analyze_EmptyCompositeInterface_ReportsEmpty()
    {
        // Composite with inherited-interface declaration but no members
        // anywhere. Should still report empty — not mixed/vestigial.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = SymbolKind.Type, FilePath = "IComposite.cs" })
            .AddSymbol(new Symbol { Id = "type:N.IMarker", Name = "IMarker", Kind = SymbolKind.Type, FilePath = "IMarker.cs" })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.IMarker", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.IComposite");

        Assert.NotNull(report);
        Assert.True(report!.IsCompositeInterface);
        Assert.Equal(0, report.AggregateMemberCount);
        Assert.Equal("empty", report.Verdict);
    }

    [Fact]
    public void Analyze_CompositeAndDirect_AggregatesBothSurfaces()
    {
        // Type with both direct members AND inherited members — verdict
        // should account for the union; live counts across both subsets.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = SymbolKind.Type, FilePath = "IComposite.cs" })
            .AddSymbol(new Symbol { Id = "type:N.ISub", Name = "ISub", Kind = SymbolKind.Type, FilePath = "ISub.cs" })
            .AddSymbol(new Symbol { Id = "method:N.IComposite.Own()", Name = "Own", Kind = SymbolKind.Method, FilePath = "IComposite.cs", ParentId = "type:N.IComposite" })
            .AddSymbol(new Symbol { Id = "method:N.ISub.Sub()", Name = "Sub", Kind = SymbolKind.Method, FilePath = "ISub.cs", ParentId = "type:N.ISub" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = SymbolKind.Method, FilePath = "Caller.cs" })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISub", Kind = EdgeKind.Inherits, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "method:N.IComposite.Own()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "type:N.ISub", TargetId = "method:N.ISub.Sub()", Kind = EdgeKind.Contains, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.IComposite.Own()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.ISub.Sub()", Kind = EdgeKind.Calls, Evidence = Evidence })
            .Build();

        var report = Analyzer().Analyze(graph, "type:N.IComposite");

        Assert.NotNull(report);
        Assert.Equal(1, report!.DirectMemberCount);
        Assert.Equal(1, report.InheritedMemberCount);
        Assert.Equal(2, report.AggregateMemberCount);
        Assert.Equal(2, report.LiveMembers);
        Assert.Equal("healthy", report.Verdict);
    }

    [Fact]
    public void Analyze_NonExistentSymbol_ReturnsNull()
    {
        var graph = new GraphBuilder().Build();
        Assert.Null(Analyzer().Analyze(graph, "type:N.Missing"));
    }

    [Fact]
    public void Analyze_NonTypeSymbol_ReturnsNull()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "method:N.Foo.Bar()", Name = "Bar", Kind = SymbolKind.Method, FilePath = "Foo.cs" })
            .Build();
        Assert.Null(Analyzer().Analyze(graph, "method:N.Foo.Bar()"));
    }

    /// <summary>
    /// Seam-discipline ratchet: the port-health algorithm must NOT live in
    /// ToolHandler.cs. Pre-F3a it occupied ~75 lines inline at
    /// HandlePortHealth (lines 812-886). The relocation is the contract.
    /// Any future regression that reintroduces the inline body fails here.
    /// INV-PORT-HEALTH-ANALYZER-SEAM-001.
    /// </summary>
    [Fact]
    public void Seam_ToolHandler_DoesNotEmbedAnalyzerBody()
    {
        var toolHandlerPath = LocateRepoFile("src/Lifeblood.Server.Mcp/ToolHandler.cs");
        Assert.True(File.Exists(toolHandlerPath),
            $"Expected ToolHandler.cs at {toolHandlerPath}.");

        var src = File.ReadAllText(toolHandlerPath);

        // The inline implementation used these algorithm-specific tokens
        // that have NO reason to appear in a thin dispatcher. The
        // refactored handler just calls _portHealth.Analyze and shapes
        // the response, so neither verdict-band string nor the local
        // memberIds/liveCount accumulator names should appear in
        // ToolHandler.cs anymore.
        Assert.DoesNotContain("\"vestigial\"", src);
        Assert.DoesNotContain("liveCount++", src);
        Assert.DoesNotContain("var memberIds = new List<string>", src);
    }

    private static string LocateRepoFile(string relativePath)
    {
        // Walk upward from the test bin/ directory until the repo root is
        // found (identified by the Lifeblood.sln marker).
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lifeblood.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    // ──────────────────────────────────────────────────────────────────
    // F3d: real-graph fixtures. Pre-F3d, F3b's composite-walk tests
    // hand-built EdgeKind.Inherits edges that the pre-F3c extractor never
    // emitted for interface→interface — so the F3b ratchet passed against
    // synthetic graphs while real C# graphs still reported composite
    // interfaces as `empty`. These fixtures compile actual C# source,
    // extract symbols + edges through the real Roslyn extractors, build a
    // SemanticGraph, run the analyzer, and assert composite-surface fields
    // come out non-zero. Closes the F3b synthetic-graph blind spot.
    // INV-EXTRACT-IFACE-INHERIT-001 + INV-PORT-HEALTH-COMPOSITE-001.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RealGraph_CompositeInterface_ReportsInheritedSurface()
    {
        var graph = ExtractRealGraph(@"
namespace App;
public interface IPart { void Run(); }
public interface IComposite : IPart { }
public class Worker : IComposite { public void Run() { } }");

        var report = Analyzer().Analyze(graph, "type:App.IComposite");

        Assert.NotNull(report);
        Assert.True(report!.IsCompositeInterface,
            "Real C# graph: interface IComposite : IPart MUST report composite, " +
            "not the pre-F3c `empty` verdict the extractor's Implements-only emission caused.");
        Assert.Contains("type:App.IPart", report.InheritedInterfaces);
        Assert.Equal(0, report.DirectMemberCount);
        Assert.Equal(1, report.InheritedMemberCount);
        Assert.Equal(1, report.AggregateMemberCount);
    }

    [Fact]
    public void RealGraph_NonCompositeInterface_ReportsZeroInherited()
    {
        var graph = ExtractRealGraph(@"
namespace App;
public interface IStandalone { void Tick(); }");

        var report = Analyzer().Analyze(graph, "type:App.IStandalone");

        Assert.NotNull(report);
        Assert.False(report!.IsCompositeInterface);
        Assert.Empty(report.InheritedInterfaces);
        Assert.Equal(1, report.DirectMemberCount);
        Assert.Equal(0, report.InheritedMemberCount);
        Assert.Equal(1, report.AggregateMemberCount);
    }

    [Fact]
    public void RealGraph_ClassImplementingInterface_NotMarkedComposite()
    {
        // Backward-compat pin: a CLASS implementing an interface emits
        // Implements (not Inherits), so the class's port-health report
        // must not pick up the interface's surface as "inherited". This
        // confirms F3c's source-kind branching at the extractor stays
        // honest for the class-source case.
        var graph = ExtractRealGraph(@"
namespace App;
public interface IRunnable { void Run(); }
public class Runner : IRunnable { public void Run() { } }");

        var report = Analyzer().Analyze(graph, "type:App.Runner");

        Assert.NotNull(report);
        Assert.False(report!.IsCompositeInterface,
            "A class implementing an interface must NOT be treated as a composite — " +
            "Implements edge kind is the discriminator post-F3c.");
        Assert.Empty(report.InheritedInterfaces);
    }

    [Fact]
    public void RealGraph_TransitiveInterfaceInheritance_WalksFullClosure()
    {
        var graph = ExtractRealGraph(@"
namespace App;
public interface IRoot { void Root(); }
public interface IMid : IRoot { void Mid(); }
public interface ILeaf : IMid { void Leaf(); }");

        var report = Analyzer().Analyze(graph, "type:App.ILeaf");

        Assert.NotNull(report);
        Assert.True(report!.IsCompositeInterface);
        Assert.Contains("type:App.IMid", report.InheritedInterfaces);
        Assert.Contains("type:App.IRoot", report.InheritedInterfaces);
        Assert.Equal(2, report.InheritedInterfaces.Length);
        // Direct: Leaf(). Inherited: Mid() + Root() = 2.
        Assert.Equal(1, report.DirectMemberCount);
        Assert.Equal(2, report.InheritedMemberCount);
        Assert.Equal(3, report.AggregateMemberCount);
    }

    /// <summary>
    /// Compile <paramref name="source"/> with the real Roslyn extractors
    /// and build a <see cref="SemanticGraph"/>. Drives the end-to-end
    /// extractor wire shape — any extractor regression that re-conflates
    /// interface inheritance with implementation lands here, not just on
    /// the F3b synthetic fixtures.
    /// </summary>
    private static SemanticGraph ExtractRealGraph(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var symbols = new RoslynSymbolExtractor()
            .Extract(model, root, "Test.cs", "file:Test.cs")
            .ToList();
        var edges = new RoslynEdgeExtractor().Extract(model, root).ToList();

        var builder = new GraphBuilder();
        foreach (var s in symbols) builder.AddSymbol(s);
        foreach (var e in edges) builder.AddEdge(e);
        return builder.Build();
    }
}
