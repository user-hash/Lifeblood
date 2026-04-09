using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Layer A2: Focused extractor tests using in-memory CSharpCompilation.
/// Proves symbol/edge extraction without needing file system or module discovery.
/// </summary>
public class RoslynExtractorTests
{
    [Fact]
    public void ExtractSymbols_ClassWithMembers()
    {
        var (model, root) = Compile(@"
namespace App;
public class Foo
{
    private int _count;
    public string Name { get; set; }
    public void DoWork() { }
    public Foo() { }
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Foo.cs", "file:Foo.cs");

        Assert.Contains(symbols, s => s.Name == "Foo" && s.Kind == DomainSymbolKind.Type);
        Assert.Contains(symbols, s => s.Name == "_count" && s.Kind == DomainSymbolKind.Field);
        Assert.Contains(symbols, s => s.Name == "Name" && s.Kind == DomainSymbolKind.Property);
        Assert.Contains(symbols, s => s.Name == "DoWork" && s.Kind == DomainSymbolKind.Method);
        Assert.Contains(symbols, s => s.Name == ".ctor" && s.Kind == DomainSymbolKind.Method);
    }

    [Fact]
    public void ExtractSymbols_Interface()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IService
{
    void Execute();
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "IService.cs", "file:IService.cs");

        var iface = symbols.FirstOrDefault(s => s.Name == "IService");
        Assert.NotNull(iface);
        Assert.Equal(DomainSymbolKind.Type, iface!.Kind);
        Assert.Equal("interface", iface.Properties["typeKind"]);
    }

    [Fact]
    public void ExtractSymbols_Enum()
    {
        var (model, root) = Compile(@"
namespace App;
public enum Color { Red, Green, Blue }");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Color.cs", "file:Color.cs");

        var enumSym = symbols.FirstOrDefault(s => s.Name == "Color");
        Assert.NotNull(enumSym);
        Assert.Equal("enum", enumSym!.Properties["typeKind"]);
    }

    [Fact]
    public void ExtractSymbols_QualifiedNames()
    {
        var (model, root) = Compile(@"
namespace MyApp.Domain;
public class Entity
{
    public string Id { get; set; }
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Entity.cs", "file:Entity.cs");

        var entity = symbols.FirstOrDefault(s => s.Name == "Entity");
        Assert.NotNull(entity);
        Assert.Equal("MyApp.Domain.Entity", entity!.QualifiedName);
    }

    [Fact]
    public void ExtractEdges_Inheritance()
    {
        var (model, root) = Compile(@"
namespace App;
public class Base { }
public class Derived : Base { }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Inherits
            && e.TargetId.Contains("Base"));
    }

    [Fact]
    public void ExtractEdges_InterfaceImplementation()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IRepo { void Save(); }
public class SqlRepo : IRepo { public void Save() { } }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("SqlRepo")
            && e.TargetId.Contains("IRepo"));
    }

    [Fact]
    public void ExtractEdges_MethodCall()
    {
        var (model, root) = Compile(@"
namespace App;
public class Logger { public void Log(string msg) { } }
public class Service
{
    private Logger _log = new Logger();
    public void Run() { _log.Log(""hello""); }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Log"));
    }

    [Fact]
    public void ExtractEdges_EvidenceIsProven()
    {
        var (model, root) = Compile(@"
namespace App;
public class A { }
public class B : A { }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.All(edges, e =>
        {
            Assert.Equal(EvidenceKind.Semantic, e.Evidence.Kind);
            Assert.Equal("Roslyn", e.Evidence.AdapterName);
        });
    }

    [Fact]
    public void ExtractEdges_TypeReference()
    {
        var (model, root) = Compile(@"
namespace App;
public class Config { public int Value; }
public class Service
{
    private Config _config;
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Config"));
    }

    [Fact]
    public void ExtractEdges_GenericTypeArgument()
    {
        var (model, root) = Compile(@"
namespace App;
public class Entity { }
public class Service
{
    private System.Collections.Generic.List<Entity> _entities;
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Entity"));
    }

    [Fact]
    public void ExtractEdges_TypeOfExpression()
    {
        var (model, root) = Compile(@"
namespace App;
public class Filter { }
public class Handler
{
    private System.Type _type = typeof(Filter);
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Handler")
            && e.TargetId.Contains("Filter"));
    }

    [Fact]
    public void ExtractEdges_AttributeType()
    {
        var (model, root) = Compile(@"
namespace App;
public class MyAttribute : System.Attribute { }
[My]
public class Service { }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("MyAttribute"));
    }

    [Fact]
    public void ExtractEdges_LocalFunctionCall_AttributedToEnclosingMethod()
    {
        var (model, root) = Compile(@"
namespace App;
public class Logger { public void Log(string msg) { } }
public class Service
{
    private Logger _log = new Logger();
    public void Run()
    {
        void Inner() { _log.Log(""from local""); }
        Inner();
    }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        // Call inside local function Inner() should be attributed to enclosing method Run(),
        // not to a dangling "Inner" symbol that doesn't exist in the graph.
        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Log"));
    }

    [Fact]
    public void ExtractEdges_ReturnType()
    {
        var (model, root) = Compile(@"
namespace App;
public class Result { }
public class Service
{
    public Result GetResult() { return null; }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Result"));
    }

    // ──────────────────────────────────────────────────────────────
    // Override edge extraction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_MethodOverride()
    {
        var (model, root) = Compile(@"
namespace App;
public class Base { public virtual void Run() { } }
public class Derived : Base { public override void Run() { } }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Overrides
            && e.SourceId.Contains("Derived") && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Base") && e.TargetId.Contains("Run"));
    }

    [Fact]
    public void ExtractEdges_MultiLevelOverride()
    {
        var (model, root) = Compile(@"
namespace App;
public class A { public virtual void Do() { } }
public class B : A { public override void Do() { } }
public class C : B { public override void Do() { } }");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        // C.Do overrides B.Do (immediate base), not A.Do
        Assert.Contains(edges, e => e.Kind == EdgeKind.Overrides
            && e.SourceId.Contains("C") && e.TargetId.Contains("B"));
        // B.Do overrides A.Do
        Assert.Contains(edges, e => e.Kind == EdgeKind.Overrides
            && e.SourceId.Contains("B") && e.TargetId.Contains("A"));
    }

    // ──────────────────────────────────────────────────────────────
    // Event symbol extraction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractSymbols_EventField()
    {
        var (model, root) = Compile(@"
namespace App;
public class Notifier
{
    public event System.EventHandler? Changed;
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Notifier.cs", "file:Notifier.cs");

        var evt = symbols.FirstOrDefault(s => s.Name == "Changed");
        Assert.NotNull(evt);
        Assert.Equal(DomainSymbolKind.Property, evt!.Kind);
        Assert.Equal("true", evt.Properties["isEvent"]);
    }

    [Fact]
    public void ExtractSymbols_InterfaceEvent()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IObservable
{
    event System.EventHandler? Updated;
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "IObservable.cs", "file:IObservable.cs");

        Assert.Contains(symbols, s => s.Name == "Updated"
            && s.Properties.ContainsKey("isEvent") && s.Properties["isEvent"] == "true");
    }

    // ──────────────────────────────────────────────────────────────
    // Indexer extraction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractSymbols_Indexer()
    {
        var (model, root) = Compile(@"
namespace App;
public class Collection
{
    public string this[int index] => """";
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Collection.cs", "file:Collection.cs");

        var indexer = symbols.FirstOrDefault(s =>
            s.Properties.TryGetValue("isIndexer", out var v) && v == "true");
        Assert.NotNull(indexer);
        Assert.Equal("this[]", indexer!.Name);
        Assert.Contains("this[", indexer.Id);
    }

    // ──────────────────────────────────────────────────────────────
    // Roslyn capability audit — verify edge extraction for all C# patterns
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_NullConditionalAccess()
    {
        // a?.Method() uses ConditionalAccessExpressionSyntax, not MemberAccessExpressionSyntax
        var (model, root) = Compile(@"
namespace App;
public class Logger { public void Log(string msg) { } }
public class Service
{
    private Logger? _log;
    public void Run() { _log?.Log(""hello""); }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Logger"));
    }

    [Fact]
    public void ExtractEdges_CastExpression()
    {
        var (model, root) = Compile(@"
namespace App;
public interface ITarget { }
public class Service
{
    public void Run(object obj) { var t = (ITarget)obj; }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("ITarget"));
    }

    [Fact]
    public void ExtractEdges_PatternMatching()
    {
        var (model, root) = Compile(@"
namespace App;
public class Target { }
public class Service
{
    public void Run(object obj) { if (obj is Target t) { } }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Target"));
    }

    [Fact]
    public void ExtractEdges_AsCast()
    {
        var (model, root) = Compile(@"
namespace App;
public class Target { }
public class Service
{
    public void Run(object obj) { var t = obj as Target; }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service")
            && e.TargetId.Contains("Target"));
    }

    [Fact]
    public void ExtractEdges_BaseMethodCall()
    {
        var (model, root) = Compile(@"
namespace App;
public class Base { public virtual void Run() { } }
public class Derived : Base
{
    public override void Run() { base.Run(); }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Derived") && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Base") && e.TargetId.Contains("Run"));
    }

    [Fact]
    public void ExtractEdges_NullConditionalCall_CreatesCallEdge()
    {
        // Sharper: verify the method CALL through ?. is captured, not just the type reference
        var (model, root) = Compile(@"
namespace App;
public class Logger { public void Log(string msg) { } }
public class Service
{
    private Logger? _log;
    public void Run() { _log?.Log(""hello""); }
}");

        var extractor = new RoslynEdgeExtractor();
        var edges = extractor.Extract(model, root);

        // ?. uses ConditionalAccessExpressionSyntax → InvocationExpressionSyntax inside
        // The Calls edge should still resolve through Roslyn's semantic model
        var hasCallEdge = edges.Any(e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Log"));
        // This test documents whether we capture the call or not.
        // If it fails, we need to add ConditionalAccessExpressionSyntax handling.
        Assert.True(hasCallEdge, "Null-conditional call _log?.Log() should produce a Calls edge");
    }

    [Fact]
    public void ExtractSymbols_OperatorOverload_ExtractedAsMethod()
    {
        var (model, root) = Compile(@"
namespace App;
public class Money
{
    public decimal Amount { get; init; }
    public static Money operator +(Money a, Money b) => new Money { Amount = a.Amount + b.Amount };
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Money.cs", "file:Money.cs");

        // OperatorDeclarationSyntax — should be extracted as a method symbol
        var opSymbol = symbols.FirstOrDefault(s => s.Kind == DomainSymbolKind.Method
            && s.Name.Contains("op_"));
        // This documents whether operators are captured. If null, we need to add them.
        Assert.NotNull(opSymbol);
    }

    [Fact]
    public void ExtractSymbols_Destructor_ExtractedAsMethod()
    {
        var (model, root) = Compile(@"
namespace App;
public class Resource
{
    ~Resource() { }
}");

        var extractor = new RoslynSymbolExtractor();
        var symbols = extractor.Extract(model, root, "Resource.cs", "file:Resource.cs");

        // DestructorDeclarationSyntax — should be extracted as a method symbol
        var dtor = symbols.FirstOrDefault(s => s.Kind == DomainSymbolKind.Method
            && (s.Name == "Finalize" || s.Name == "~Resource"));
        // This documents whether destructors are captured. If null, we need to add them.
        Assert.NotNull(dtor);
    }

    // ──────────────────────────────────────────────────────────────
    // Cross-assembly edge extraction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_CrossAssembly_TypeReference()
    {
        // Module A: defines IPort interface
        var moduleASource = @"
namespace ModuleA;
public interface IPort { void Execute(); }";

        // Module B: references IPort from Module A (via metadata)
        var moduleBSource = @"
using ModuleA;
namespace ModuleB;
public class Adapter : IPort { public void Execute() { } }";

        var (modelB, rootB) = CompileCrossAssembly("ModuleA", moduleASource, "ModuleB", moduleBSource);

        var extractor = new RoslynEdgeExtractor
        {
            KnownModuleAssemblies = new HashSet<string>(StringComparer.Ordinal) { "ModuleA", "ModuleB" }
        };
        var edges = extractor.Extract(modelB, rootB);

        // Adapter should have an Implements edge pointing to ModuleA's IPort
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("Adapter")
            && e.TargetId.Contains("IPort"));
    }

    [Fact]
    public void ExtractEdges_CrossAssembly_MethodCall()
    {
        var moduleASource = @"
namespace ModuleA;
public class Logger { public void Log(string msg) { } }";

        var moduleBSource = @"
using ModuleA;
namespace ModuleB;
public class Service
{
    private Logger _log = new Logger();
    public void Run() { _log.Log(""hello""); }
}";

        var (modelB, rootB) = CompileCrossAssembly("ModuleA", moduleASource, "ModuleB", moduleBSource);

        var extractor = new RoslynEdgeExtractor
        {
            KnownModuleAssemblies = new HashSet<string>(StringComparer.Ordinal) { "ModuleA", "ModuleB" }
        };
        var edges = extractor.Extract(modelB, rootB);

        // Cross-module method call: Service.Run → Logger.Log
        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Run")
            && e.TargetId.Contains("Log"));
    }

    [Fact]
    public void ExtractEdges_CrossAssembly_WithoutKnownModules_NoEdges()
    {
        // Without KnownModuleAssemblies set, cross-module edges should NOT be created
        var moduleASource = @"
namespace ModuleA;
public interface IPort { }";

        var moduleBSource = @"
using ModuleA;
namespace ModuleB;
public class Adapter : IPort { }";

        var (modelB, rootB) = CompileCrossAssembly("ModuleA", moduleASource, "ModuleB", moduleBSource);

        var extractor = new RoslynEdgeExtractor(); // No KnownModuleAssemblies
        var edges = extractor.Extract(modelB, rootB);

        // Should NOT have cross-module Implements edge (old behavior preserved)
        Assert.DoesNotContain(edges, e => e.Kind == EdgeKind.Implements
            && e.TargetId.Contains("IPort"));
    }

    [Fact]
    public void ExtractEdges_CrossAssembly_BclTypesStillFiltered()
    {
        // Even with KnownModuleAssemblies, System.* types should be filtered
        var moduleBSource = @"
namespace ModuleB;
public class Service
{
    private System.Collections.Generic.List<string> _items = new();
}";

        var tree = CSharpSyntaxTree.ParseText(moduleBSource);
        var refList = BclRefs();
        var compilation = CSharpCompilation.Create("ModuleB",
            new[] { tree }, refList,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);

        var extractor = new RoslynEdgeExtractor
        {
            KnownModuleAssemblies = new HashSet<string>(StringComparer.Ordinal) { "ModuleB" }
        };
        var edges = extractor.Extract(model, tree.GetRoot());

        // No edges to System types
        Assert.DoesNotContain(edges, e => e.TargetId.Contains("System."));
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static (SemanticModel model, SyntaxNode root) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { tree }, BclRefs(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        return (model, tree.GetRoot());
    }

    /// <summary>
    /// Compile two modules where Module B references Module A via PE metadata.
    /// Returns the semantic model for Module B — the consumer side where cross-module
    /// edges need to be extracted.
    /// </summary>
    private static (SemanticModel model, SyntaxNode root) CompileCrossAssembly(
        string moduleAName, string moduleASource,
        string moduleBName, string moduleBSource)
    {
        var bclRefs = BclRefs();

        // Compile Module A
        var treeA = CSharpSyntaxTree.ParseText(moduleASource);
        var compilationA = CSharpCompilation.Create(moduleAName,
            new[] { treeA }, bclRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Downgrade Module A to PE reference (same as ModuleCompilationBuilder does)
        MetadataReference moduleARef;
        using (var ms = new MemoryStream())
        {
            var emitResult = compilationA.Emit(ms);
            Assert.True(emitResult.Success,
                $"Module A compilation failed: {string.Join(", ", emitResult.Diagnostics)}");
            moduleARef = MetadataReference.CreateFromImage(ms.ToArray());
        }

        // Compile Module B with Module A as metadata reference
        var treeB = CSharpSyntaxTree.ParseText(moduleBSource);
        var refsB = new List<MetadataReference>(bclRefs) { moduleARef };
        var compilationB = CSharpCompilation.Create(moduleBName,
            new[] { treeB }, refsB,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var modelB = compilationB.GetSemanticModel(treeB);
        return (modelB, treeB.GetRoot());
    }

    private static List<MetadataReference> BclRefs()
    {
        var refList = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            var sr = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(sr)) refList.Add(MetadataReference.CreateFromFile(sr));
        }
        return refList;
    }
}
