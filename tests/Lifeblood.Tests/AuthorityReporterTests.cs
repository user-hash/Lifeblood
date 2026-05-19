using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Coverage for v0.6.7 P5 — authority report, forwarder classification,
/// port health, cycles. Tests use minimal in-memory graphs so failures
/// pinpoint exactly which counting rule broke.
/// </summary>
public class AuthorityReporterTests
{
    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    // ──────────────────────────────────────────────────────────────────
    // Authority report
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorityReport_TypeWithNoInterfaces_ZeroImplementedCount()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Foo", Name = "Foo", QualifiedName = "N.Foo", Kind = DomainSymbolKind.Type })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Foo");
        Assert.Equal("type:N.Foo", report.TypeId);
        Assert.Equal(0, report.ImplementedInterfaceCount);
        Assert.Empty(report.PerInterface);
    }

    [Fact]
    public void AuthorityReport_TypeImplementsTwoInterfaces_ReportsBoth()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IA", Name = "IA", QualifiedName = "N.IA", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.IB", Name = "IB", QualifiedName = "N.IB", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", QualifiedName = "N.Host", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.IA", Kind = EdgeKind.Implements, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.IB", Kind = EdgeKind.Implements, Evidence = Ev })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");
        Assert.Equal(2, report.ImplementedInterfaceCount);
        Assert.Equal(2, report.PerInterface.Length);
    }

    [Fact]
    public void AuthorityReport_OwnedPublicSurface_CountsPublicMembers()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:N.Host.PublicM()", Name = "PublicM", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host", Visibility = Visibility.Public })
            .AddSymbol(new Symbol { Id = "method:N.Host.PrivateM()", Name = "PrivateM", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host", Visibility = Visibility.Private })
            .AddSymbol(new Symbol { Id = "property:N.Host.P", Name = "P", Kind = DomainSymbolKind.Property, ParentId = "type:N.Host", Visibility = Visibility.Public })
            .AddSymbol(new Symbol { Id = "type:N.Host.Nested", Name = "Nested", Kind = DomainSymbolKind.Type, ParentId = "type:N.Host", Visibility = Visibility.Public })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.PublicM()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.PrivateM()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "property:N.Host.P", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.Host.Nested", Kind = EdgeKind.Contains, Evidence = Ev })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");
        // PublicM + P; PrivateM excluded (visibility); Nested excluded (type, not member).
        Assert.Equal(2, report.OwnedPublicSurface);
    }

    [Fact]
    public void AuthorityReport_ForwarderRatio_ReadsFromExtractorClassification()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Host.A()", Name = "A", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host",
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["classification"] = "PureForwarder" },
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Host.B()", Name = "B", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host",
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["classification"] = "PureForwarder" },
            })
            .AddSymbol(new Symbol
            {
                Id = "method:N.Host.C()", Name = "C", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host",
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["classification"] = "RealLogic" },
            })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.A()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.B()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.C()", Kind = EdgeKind.Contains, Evidence = Ev })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");
        Assert.Equal(3, report.TotalMethodCount);
        Assert.Equal(2, report.PureForwarderCount);
        Assert.Equal(0.667, report.ForwarderRatio, 2);
    }

    [Fact]
    public void AuthorityReport_NoClassificationData_ReturnsSentinelMinusOne()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:N.Host.M()", Name = "M", Kind = DomainSymbolKind.Method, ParentId = "type:N.Host" })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "method:N.Host.M()", Kind = EdgeKind.Contains, Evidence = Ev })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");
        Assert.Equal(-1.0, report.ForwarderRatio);
    }

    [Fact]
    public void AuthorityReport_NonExistentType_ReturnsEmptyReport()
    {
        var graph = new GraphBuilder().Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:Does.Not.Exist");
        Assert.Equal(0, report.ImplementedInterfaceCount);
        Assert.Equal(-1.0, report.ForwarderRatio);
    }

    // ──────────────────────────────────────────────────────────────────
    // Method-classification extractor recording (forwarder pattern)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Extractor_ExpressionBodiedInvocation_ClassifiesAsPureForwarder()
    {
        var (model, root) = Compile(@"
namespace App;
public class Wrapper {
    private System.IO.TextWriter _w;
    public void Write(string s) => _w.Write(s);
}");
        var symbols = new RoslynSymbolExtractor().Extract(model, root, "Wrapper.cs", "file:Wrapper.cs");
        var write = symbols.First(s => s.Name == "Write");
        Assert.True(write.Properties.TryGetValue("classification", out var c));
        Assert.Equal("PureForwarder", c);
    }

    [Fact]
    public void Extractor_SingleStatementInvocation_ClassifiesAsPureForwarder()
    {
        var (model, root) = Compile(@"
namespace App;
public class Forwarder {
    private System.IO.TextWriter _w;
    public void Write(string s) { _w.Write(s); }
}");
        var symbols = new RoslynSymbolExtractor().Extract(model, root, "F.cs", "file:F.cs");
        var write = symbols.First(s => s.Name == "Write");
        Assert.Equal("PureForwarder", write.Properties["classification"]);
    }

    [Fact]
    public void Extractor_NullGuardThenDelegate_ClassifiesAsThinWrapper()
    {
        var (model, root) = Compile(@"
namespace App;
public class Wrap {
    private string _x;
    public void Touch() {
        if (_x == null) return;
        System.Console.WriteLine(_x);
    }
}");
        var symbols = new RoslynSymbolExtractor().Extract(model, root, "T.cs", "file:T.cs");
        var touch = symbols.First(s => s.Name == "Touch");
        Assert.Equal("ThinWrapper", touch.Properties["classification"]);
    }

    [Fact]
    public void Extractor_MultipleInvocations_ClassifiesAsRealLogic()
    {
        var (model, root) = Compile(@"
namespace App;
public class Real {
    public int Compute(int x) {
        var a = System.Math.Abs(x);
        var b = System.Math.Sqrt(a);
        return (int)b;
    }
}");
        var symbols = new RoslynSymbolExtractor().Extract(model, root, "R.cs", "file:R.cs");
        var c = symbols.First(s => s.Name == "Compute");
        Assert.Equal("RealLogic", c.Properties["classification"]);
    }

    [Fact]
    public void Extractor_AbstractMethod_NoClassification()
    {
        var (model, root) = Compile(@"
namespace App;
public abstract class Foo {
    public abstract void M();
}");
        var symbols = new RoslynSymbolExtractor().Extract(model, root, "Foo.cs", "file:Foo.cs");
        var m = symbols.First(s => s.Name == "M");
        Assert.False(m.Properties.ContainsKey("classification"));
    }

    private static (SemanticModel model, SyntaxNode root) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation.GetSemanticModel(tree), tree.GetRoot());
    }

    // ──────────────────────────────────────────────────────────────────
    // S7: planning-verdict evidence fields. ADDITIVE — every existing
    // metric stays stable; new fields surface the partition between
    // same-module / cross-module consumers and the
    // single-implementer signal so callers can derive
    // EvictableDebt / BoundaryContract / SceneDiscoveryContract /
    // CompositeFacade / AdapterShimOnly / NeedsAudit verdicts client-side
    // per the composition recipe (INV-AUTHORITY-PLANNING-COMPOSITION-001).
    // Lifeblood ships evidence, not verdicts — pattern matches
    // INV-FLAG-COVERAGE-COMPOSITION-001.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorityReport_BoundaryShape_HighCrossAssembly_LowSameAssembly()
    {
        // Type lives in ModuleA, consumed by two distinct other modules.
        // Boundary contract evidence shape.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:ModuleA", Name = "ModuleA", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:ModuleB", Name = "ModuleB", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "mod:ModuleC", Name = "ModuleC", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:N.Boundary", Name = "Boundary", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleA" })
            .AddSymbol(new Symbol { Id = "method:N.B.X()", Name = "X", Kind = DomainSymbolKind.Method, ParentId = "type:N.B" })
            .AddSymbol(new Symbol { Id = "type:N.B", Name = "B", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleB" })
            .AddSymbol(new Symbol { Id = "method:N.C.Y()", Name = "Y", Kind = DomainSymbolKind.Method, ParentId = "type:N.C" })
            .AddSymbol(new Symbol { Id = "type:N.C", Name = "C", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleC" })
            .AddEdge(new Edge { SourceId = "method:N.B.X()", TargetId = "type:N.Boundary", Kind = EdgeKind.References, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "method:N.C.Y()", TargetId = "type:N.Boundary", Kind = EdgeKind.References, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Boundary");

        Assert.Equal(2, report.CrossAssemblyConsumerCount); // ModuleB + ModuleC
        Assert.Equal(0, report.SameAssemblyConsumerCount);  // no same-module use
    }

    [Fact]
    public void AuthorityReport_AdapterShimShape_HighSameAssembly_ZeroCrossAssembly()
    {
        // Type lives in ModuleA, consumed only by other symbols in
        // ModuleA. Adapter-shim evidence shape.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:ModuleA", Name = "ModuleA", Kind = DomainSymbolKind.Module })
            .AddSymbol(new Symbol { Id = "type:N.Shim", Name = "Shim", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleA" })
            .AddSymbol(new Symbol { Id = "type:N.Local1", Name = "Local1", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleA" })
            .AddSymbol(new Symbol { Id = "method:N.Local1.A()", Name = "A", Kind = DomainSymbolKind.Method, ParentId = "type:N.Local1" })
            .AddSymbol(new Symbol { Id = "type:N.Local2", Name = "Local2", Kind = DomainSymbolKind.Type, ParentId = "mod:ModuleA" })
            .AddSymbol(new Symbol { Id = "method:N.Local2.B()", Name = "B", Kind = DomainSymbolKind.Method, ParentId = "type:N.Local2" })
            .AddEdge(new Edge { SourceId = "method:N.Local1.A()", TargetId = "type:N.Shim", Kind = EdgeKind.References, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "method:N.Local2.B()", TargetId = "type:N.Shim", Kind = EdgeKind.References, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Shim");

        Assert.Equal(0, report.CrossAssemblyConsumerCount);
        Assert.Equal(2, report.SameAssemblyConsumerCount); // both local methods
    }

    [Fact]
    public void AuthorityReport_NoModuleAncestor_DegradesGracefullyToZero()
    {
        // JSON-imported graph without containment chain — must not
        // throw; returns zero counts.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Orphan", Name = "Orphan", Kind = DomainSymbolKind.Type })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Orphan");
        Assert.Equal(0, report.CrossAssemblyConsumerCount);
        Assert.Equal(0, report.SameAssemblyConsumerCount);
    }

    [Fact]
    public void AuthorityReport_HasSingleImplementer_TrueForInterfaceWithOneImpl()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.IPort", Name = "IPort", Kind = DomainSymbolKind.Type,
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["typeKind"] = "interface" },
            })
            .AddSymbol(new Symbol { Id = "type:N.Adapter", Name = "Adapter", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:N.Adapter", TargetId = "type:N.IPort", Kind = EdgeKind.Implements, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.IPort");
        Assert.True(report.HasSingleImplementer);
    }

    [Fact]
    public void AuthorityReport_HasSingleImplementer_FalseForInterfaceWithMultipleImpls()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.IPort", Name = "IPort", Kind = DomainSymbolKind.Type,
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["typeKind"] = "interface" },
            })
            .AddSymbol(new Symbol { Id = "type:N.AdapterA", Name = "AdapterA", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.AdapterB", Name = "AdapterB", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:N.AdapterA", TargetId = "type:N.IPort", Kind = EdgeKind.Implements, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.AdapterB", TargetId = "type:N.IPort", Kind = EdgeKind.Implements, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.IPort");
        Assert.False(report.HasSingleImplementer);
    }

    [Fact]
    public void AuthorityReport_HasSingleImplementer_NullForNonInterface()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:N.Concrete", Name = "Concrete", Kind = DomainSymbolKind.Type,
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["typeKind"] = "class" },
            })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Concrete");
        Assert.Null(report.HasSingleImplementer);
    }

    [Fact]
    public void AuthorityReport_HasSingleImplementer_NullWhenTypeKindMissing()
    {
        // Hand-built graph without typeKind property (older JSON
        // imports) — must not claim single-implementer status it
        // cannot verify.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.Mystery", Name = "Mystery", Kind = DomainSymbolKind.Type })
            .Build();
        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Mystery");
        Assert.Null(report.HasSingleImplementer);
    }

    [Fact]
    public void AuthorityReport_RealGraph_PortWithSingleImplementer_SurfacedAsAdapterShimCandidate()
    {
        // End-to-end through production extractor. Single-implementer
        // interface = caller can derive AdapterShimOnly verdict.
        var (model, root) = Compile(@"
namespace App;
public interface IRunner { void Run(); }
public class Runner : IRunner { public void Run() { } }
public class Caller { public void Use(IRunner r) { r.Run(); } }");
        var symbols = new RoslynSymbolExtractor()
            .Extract(model, root, "App.cs", "file:App.cs")
            .ToList();
        var edges = new RoslynEdgeExtractor().Extract(model, root).ToList();
        var builder = new GraphBuilder();
        // Add module ancestor so the same/cross-assembly walk has
        // something to resolve to.
        builder.AddSymbol(new Symbol { Id = "mod:TestAssembly", Name = "TestAssembly", Kind = DomainSymbolKind.Module });
        builder.AddSymbol(new Symbol { Id = "file:App.cs", Name = "App.cs", Kind = DomainSymbolKind.File, ParentId = "mod:TestAssembly" });
        foreach (var s in symbols)
        {
            // Re-parent file-rooted top-level types so module resolution
            // works on the synthetic test setup.
            var withParent = string.IsNullOrEmpty(s.ParentId) || s.ParentId == "file:App.cs"
                ? new Symbol
                {
                    Id = s.Id, Name = s.Name, QualifiedName = s.QualifiedName, Kind = s.Kind,
                    FilePath = s.FilePath, Line = s.Line, ParentId = "file:App.cs",
                    Visibility = s.Visibility, IsAbstract = s.IsAbstract, IsStatic = s.IsStatic,
                    Properties = s.Properties,
                }
                : s;
            builder.AddSymbol(withParent);
        }
        foreach (var e in edges) builder.AddEdge(e);
        var graph = builder.Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:App.IRunner");
        Assert.True(report.HasSingleImplementer,
            "IRunner has exactly one source-defined implementer (Runner) — must surface as single-implementer.");
    }

    // ──────────────────────────────────────────────────────────────────
    // F3e: per-interface composite / inherited surface in InterfaceUsage.
    // ABG-style composite-facade ports (an interface that aggregates 3+
    // sub-interfaces with little or no surface of its own) used to report
    // MemberCount: 0 even when the inherited contract carried the real
    // load-bearing surface. F3e adds DirectMemberCount /
    // InheritedMemberCount / AggregateMemberCount /
    // InheritedInterfaces[] / IsCompositeInterface to InterfaceUsage,
    // sharing the transitive walker with port_health's F3b.
    // INV-AUTHORITY-COMPOSITE-001.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorityReport_CompositeInterfaceFacade_AggregatesInheritedSurface()
    {
        // Host implements one composite facade IComposite that itself
        // inherits two sub-interfaces with 1 + 1 = 2 member surface.
        // Pre-F3e perInterface[0].MemberCount was 0; post-F3e the
        // aggregate is 2 with inheritedInterfaces populated.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.ISubA", Name = "ISubA", QualifiedName = "N.ISubA", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.ISubB", Name = "ISubB", QualifiedName = "N.ISubB", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", QualifiedName = "N.IComposite", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", QualifiedName = "N.Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:N.ISubA.Run()", Name = "Run", Kind = DomainSymbolKind.Method, ParentId = "type:N.ISubA" })
            .AddSymbol(new Symbol { Id = "method:N.ISubB.Tick()", Name = "Tick", Kind = DomainSymbolKind.Method, ParentId = "type:N.ISubB" })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.IComposite", Kind = EdgeKind.Implements, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISubA", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISubB", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.ISubA", TargetId = "method:N.ISubA.Run()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.ISubB", TargetId = "method:N.ISubB.Tick()", Kind = EdgeKind.Contains, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");

        Assert.Equal(1, report.ImplementedInterfaceCount);
        Assert.Single(report.PerInterface);
        var row = report.PerInterface[0];
        Assert.Equal("type:N.IComposite", row.InterfaceId);
        Assert.True(row.IsCompositeInterface,
            "Composite facade with 0 direct + 2 inherited members must be flagged composite.");
        Assert.Equal(0, row.DirectMemberCount);
        Assert.Equal(2, row.InheritedMemberCount);
        Assert.Equal(2, row.AggregateMemberCount);
        Assert.Equal(2, row.MemberCount); // backwards-compatible alias
        Assert.Equal(new[] { "type:N.ISubA", "type:N.ISubB" }, row.InheritedInterfaces);
    }

    [Fact]
    public void AuthorityReport_NonCompositeInterface_ReportsZeroInheritedSurface()
    {
        // Backward-compat: pre-F3e shape for a flat interface.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IFlat", Name = "IFlat", QualifiedName = "N.IFlat", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", QualifiedName = "N.Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:N.IFlat.M()", Name = "M", Kind = DomainSymbolKind.Method, ParentId = "type:N.IFlat" })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.IFlat", Kind = EdgeKind.Implements, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.IFlat", TargetId = "method:N.IFlat.M()", Kind = EdgeKind.Contains, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");

        var row = Assert.Single(report.PerInterface);
        Assert.False(row.IsCompositeInterface);
        Assert.Equal(1, row.DirectMemberCount);
        Assert.Equal(0, row.InheritedMemberCount);
        Assert.Equal(1, row.AggregateMemberCount);
        Assert.Equal(1, row.MemberCount);
        Assert.Empty(row.InheritedInterfaces);
    }

    [Fact]
    public void AuthorityReport_CompositeInterfaceConsumers_CountAcrossAggregate()
    {
        // A caller of an inherited member counts as a consumer of the
        // composite facade — the caller is reaching the contract.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.ISub", Name = "ISub", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.IComposite", Name = "IComposite", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:N.Host", Name = "Host", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "method:N.ISub.Run()", Name = "Run", Kind = DomainSymbolKind.Method, ParentId = "type:N.ISub" })
            .AddSymbol(new Symbol { Id = "method:N.Caller.X()", Name = "X", Kind = DomainSymbolKind.Method })
            .AddEdge(new Edge { SourceId = "type:N.Host", TargetId = "type:N.IComposite", Kind = EdgeKind.Implements, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.IComposite", TargetId = "type:N.ISub", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:N.ISub", TargetId = "method:N.ISub.Run()", Kind = EdgeKind.Contains, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "method:N.Caller.X()", TargetId = "method:N.ISub.Run()", Kind = EdgeKind.Calls, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.Host");
        var row = Assert.Single(report.PerInterface);

        Assert.Equal(1, row.ConsumerCount); // pre-F3e: 0 (didn't walk inherited members)
        Assert.True(row.IsCompositeInterface);
    }

    [Fact]
    public void AuthorityReport_InterfaceTypeAsHost_FindsParentInterfacesPostF3c()
    {
        // Regression pin for F3c: when the type under analysis is itself
        // an interface (extracted with typeKind="interface"), the
        // implemented-interface collection must walk Inherits, not
        // Implements. Pre-F3c the reporter only walked Implements,
        // post-F3c it branches on source typeKind.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:N.IBase", Name = "IBase", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "type:N.IChild", Name = "IChild", Kind = DomainSymbolKind.Type,
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["typeKind"] = "interface" },
            })
            .AddEdge(new Edge { SourceId = "type:N.IChild", TargetId = "type:N.IBase", Kind = EdgeKind.Inherits, Evidence = Ev })
            .Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:N.IChild");

        Assert.Equal(1, report.ImplementedInterfaceCount);
        Assert.Equal("type:N.IBase", report.PerInterface[0].InterfaceId);
    }

    [Fact]
    public void AuthorityReport_RealGraph_CompositeHost_AggregatesInheritedMembers()
    {
        // End-to-end: compile real C# source, extract through the
        // production pipeline, run authority_report. ABG-style facade.
        var (model, root) = Compile(@"
namespace App;
public interface IPartA { void RunA(); }
public interface IPartB { void RunB(); }
public interface IFacade : IPartA, IPartB { }
public class Host : IFacade {
    public void RunA() { }
    public void RunB() { }
}");
        var symbols = new RoslynSymbolExtractor()
            .Extract(model, root, "Facade.cs", "file:Facade.cs")
            .ToList();
        var edges = new RoslynEdgeExtractor().Extract(model, root).ToList();
        var builder = new GraphBuilder();
        foreach (var s in symbols) builder.AddSymbol(s);
        foreach (var e in edges) builder.AddEdge(e);
        var graph = builder.Build();

        var report = new LifebloodAuthorityReporter().Analyze(graph, "type:App.Host");

        Assert.Equal(1, report.ImplementedInterfaceCount);
        var row = report.PerInterface[0];
        Assert.Equal("type:App.IFacade", row.InterfaceId);
        Assert.True(row.IsCompositeInterface,
            "Real C# graph: IFacade : IPartA, IPartB MUST be reported composite. " +
            "Pre-F3c+F3e the inherited surface was invisible.");
        Assert.Equal(2, row.InheritedInterfaces.Length);
        Assert.Equal(0, row.DirectMemberCount);
        Assert.Equal(2, row.InheritedMemberCount);
        Assert.Equal(2, row.AggregateMemberCount);
    }
}
