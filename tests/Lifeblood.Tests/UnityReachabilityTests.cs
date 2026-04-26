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
/// Coverage for the v0.6.7 Unity reachability provider (P3 / INV-UNITY-001).
/// Uses synthetic fixtures so the tests run without a Unity install.
///
/// Three classes of dispatch are pinned:
///   1. Methods marked with Unity entrypoint attributes
///      (RuntimeInitializeOnLoadMethod, MenuItem, etc.).
///   2. MonoBehaviour magic methods on a type that transitively
///      inherits from MonoBehaviour / ScriptableObject / Editor.
///   3. Negative coverage: non-Unity methods on non-Unity types
///      remain marked dead-code candidates.
/// </summary>
public class UnityReachabilityTests
{
    private static readonly Evidence Ev = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    [Fact]
    public void Adapter_RuntimeInitializeOnLoadMethod_FlaggedAsReachable()
    {
        var sym = new Symbol
        {
            Id = "method:App.Boot.Init()",
            Name = "Init",
            Kind = DomainSymbolKind.Method,
            ParentId = "type:App.Boot",
            Properties = new System.Collections.Generic.Dictionary<string, string> { ["attributes"] = "RuntimeInitializeOnLoadMethod" },
        };
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.Boot", Name = "Boot", QualifiedName = "App.Boot", Kind = DomainSymbolKind.Type })
            .AddSymbol(sym)
            .Build();

        var adapter = new UnityReachabilityAdapter();
        var hit = adapter.IsRuntimeReachable(graph, sym, out var reason);
        Assert.True(hit);
        Assert.Contains("RuntimeInitializeOnLoadMethod", reason);
    }

    [Fact]
    public void Adapter_MenuItem_FlaggedAsReachable()
    {
        var sym = new Symbol
        {
            Id = "method:App.Tools.Open()",
            Name = "Open",
            Kind = DomainSymbolKind.Method,
            ParentId = "type:App.Tools",
            Properties = new System.Collections.Generic.Dictionary<string, string> { ["attributes"] = "MenuItem" },
        };
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.Tools", Name = "Tools", QualifiedName = "App.Tools", Kind = DomainSymbolKind.Type })
            .AddSymbol(sym)
            .Build();

        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out var reason);
        Assert.True(hit);
        Assert.Contains("MenuItem", reason);
    }

    [Fact]
    public void Adapter_MonoBehaviourMagicMethod_OnDirectSubclass_FlaggedAsReachable()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:UnityEngine.MonoBehaviour", Name = "MonoBehaviour", QualifiedName = "UnityEngine.MonoBehaviour", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Player", Name = "Player", QualifiedName = "App.Player", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App.Player", TargetId = "type:UnityEngine.MonoBehaviour", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Player.Update()",
                Name = "Update",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Player",
            })
            .Build();

        var sym = graph.GetSymbol("method:App.Player.Update()")!;
        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out var reason);
        Assert.True(hit);
        Assert.Contains("Update", reason);
    }

    [Fact]
    public void Adapter_MonoBehaviourMagicMethod_OnTransitiveSubclass_FlaggedAsReachable()
    {
        // App.Enemy -> App.Character -> UnityEngine.MonoBehaviour
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:UnityEngine.MonoBehaviour", Name = "MonoBehaviour", QualifiedName = "UnityEngine.MonoBehaviour", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Character", Name = "Character", QualifiedName = "App.Character", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Enemy", Name = "Enemy", QualifiedName = "App.Enemy", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App.Character", TargetId = "type:UnityEngine.MonoBehaviour", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:App.Enemy",     TargetId = "type:App.Character",            Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Enemy.OnTriggerEnter()",
                Name = "OnTriggerEnter",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Enemy",
            })
            .Build();

        var sym = graph.GetSymbol("method:App.Enemy.OnTriggerEnter()")!;
        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out _);
        Assert.True(hit);
    }

    [Fact]
    public void Adapter_PlainMethod_OnPlainType_NotFlagged()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.Calculator", Name = "Calculator", QualifiedName = "App.Calculator", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Calculator.Helper()",
                Name = "Helper",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Calculator",
            })
            .Build();

        var sym = graph.GetSymbol("method:App.Calculator.Helper()")!;
        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out var reason);
        Assert.False(hit);
        Assert.Empty(reason);
    }

    [Fact]
    public void Adapter_MagicMethodName_OnNonMonoBehaviourType_NotFlagged()
    {
        // "Update" is not magic if the type doesn't inherit from a Unity base.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.Plain", Name = "Plain", QualifiedName = "App.Plain", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Plain.Update()",
                Name = "Update",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Plain",
            })
            .Build();

        var sym = graph.GetSymbol("method:App.Plain.Update()")!;
        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out _);
        Assert.False(hit);
    }

    [Fact]
    public void Adapter_InheritsCycle_DoesNotHang()
    {
        // Malformed graph: A -> B -> A. The adapter must terminate.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:App.A", Name = "A", QualifiedName = "App.A", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.B", Name = "B", QualifiedName = "App.B", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App.A", TargetId = "type:App.B", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddEdge(new Edge { SourceId = "type:App.B", TargetId = "type:App.A", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddSymbol(new Symbol
            {
                Id = "method:App.A.Update()",
                Name = "Update",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.A",
            })
            .Build();

        var sym = graph.GetSymbol("method:App.A.Update()")!;
        var hit = new UnityReachabilityAdapter().IsRuntimeReachable(graph, sym, out _);
        Assert.False(hit);
    }

    // ──────────────────────────────────────────────────────────────────
    // Integration with LifebloodDeadCodeAnalyzer
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void DeadCodeAnalyzer_WithUnityProvider_ExcludesMagicMethodsAndAttributedEntrypoints()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:UnityEngine.MonoBehaviour", Name = "MonoBehaviour", QualifiedName = "UnityEngine.MonoBehaviour", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Boot", Name = "Boot", QualifiedName = "App.Boot", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Player", Name = "Player", QualifiedName = "App.Player", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App.Player", TargetId = "type:UnityEngine.MonoBehaviour", Kind = EdgeKind.Inherits, Evidence = Ev })
            // Internal methods, no incoming references — would be dead without the provider.
            .AddSymbol(new Symbol
            {
                Id = "method:App.Boot.Init()",
                Name = "Init",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Boot",
                Visibility = Visibility.Internal,
                FilePath = "Boot.cs",
                Properties = new System.Collections.Generic.Dictionary<string, string> { ["attributes"] = "RuntimeInitializeOnLoadMethod" },
            })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Player.Update()",
                Name = "Update",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Player",
                Visibility = Visibility.Internal,
                FilePath = "Player.cs",
            })
            // Plain unused helper — should still surface.
            .AddSymbol(new Symbol
            {
                Id = "method:App.Boot.UnusedHelper()",
                Name = "UnusedHelper",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Boot",
                Visibility = Visibility.Internal,
                FilePath = "Boot.cs",
            })
            .Build();

        IUnityReachabilityProvider unity = new UnityReachabilityAdapter();
        var analyzer = new LifebloodDeadCodeAnalyzer(unity);
        var findings = analyzer.FindDeadCode(graph, new DeadCodeOptions());

        Assert.Contains(findings, f => f.CanonicalId == "method:App.Boot.UnusedHelper()");
        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:App.Boot.Init()");
        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:App.Player.Update()");
    }

    [Fact]
    public void DeadCodeAnalyzer_WithoutUnityProvider_StillFlagsMagicMethodsAsDead()
    {
        // No provider injected — analyzer behaves as it did pre-P3.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:UnityEngine.MonoBehaviour", Name = "MonoBehaviour", QualifiedName = "UnityEngine.MonoBehaviour", Kind = DomainSymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:App.Player", Name = "Player", QualifiedName = "App.Player", Kind = DomainSymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:App.Player", TargetId = "type:UnityEngine.MonoBehaviour", Kind = EdgeKind.Inherits, Evidence = Ev })
            .AddSymbol(new Symbol
            {
                Id = "method:App.Player.Update()",
                Name = "Update",
                Kind = DomainSymbolKind.Method,
                ParentId = "type:App.Player",
                Visibility = Visibility.Internal,
                FilePath = "Player.cs",
            })
            .Build();

        var analyzer = new LifebloodDeadCodeAnalyzer();
        var findings = analyzer.FindDeadCode(graph, new DeadCodeOptions());
        Assert.Contains(findings, f => f.CanonicalId == "method:App.Player.Update()");
    }

    // ──────────────────────────────────────────────────────────────────
    // Extractor integration — the C# adapter records attribute names
    // on methods + types so the reachability provider can read them.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Extractor_RecordsMethodAttributes_OnPropertiesDictionary()
    {
        var (model, root) = Compile(@"
namespace App;
public class Bootstrap
{
    [System.Obsolete]
    public void Old() { }
}");

        var symbols = new RoslynSymbolExtractor().Extract(model, root, "Bootstrap.cs", "file:Bootstrap.cs");
        var oldMethod = symbols.FirstOrDefault(s => s.Name == "Old");
        Assert.NotNull(oldMethod);
        Assert.True(oldMethod!.Properties.TryGetValue("attributes", out var attrs));
        Assert.Equal("Obsolete", attrs);
    }

    [Fact]
    public void Extractor_RecordsTypeAttributes_OnPropertiesDictionary()
    {
        var (model, root) = Compile(@"
namespace App;
[System.Serializable]
public class Config { }");

        var symbols = new RoslynSymbolExtractor().Extract(model, root, "Config.cs", "file:Config.cs");
        var type = symbols.FirstOrDefault(s => s.Name == "Config" && s.Kind == DomainSymbolKind.Type);
        Assert.NotNull(type);
        Assert.True(type!.Properties.TryGetValue("attributes", out var attrs));
        Assert.Equal("Serializable", attrs);
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
