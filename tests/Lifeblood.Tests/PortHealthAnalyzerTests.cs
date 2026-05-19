using System.IO;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

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
}
