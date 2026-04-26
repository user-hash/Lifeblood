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
}
