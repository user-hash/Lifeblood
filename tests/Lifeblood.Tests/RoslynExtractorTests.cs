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
    // BUG-004: Method-level Implements edges
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_InterfaceMethodImpl_EmitsMethodLevelImplementsEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IRepo { void Save(); }
public class SqlRepo : IRepo { public void Save() { } }");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        // Type-level Implements edge (existing behavior)
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId == "type:App.SqlRepo" && e.TargetId == "type:App.IRepo");

        // Method-level Implements edge (new — BUG-004 fix)
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId == "method:App.SqlRepo.Save()" && e.TargetId == "method:App.IRepo.Save()");
    }

    [Fact]
    public void ExtractEdges_InterfacePropertyImpl_EmitsPropertyLevelImplementsEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IReader { string Name { get; } }
public class ConcreteReader : IReader { public string Name => ""x""; }");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId == "property:App.ConcreteReader.Name"
            && e.TargetId == "property:App.IReader.Name");
    }

    [Fact]
    public void ExtractEdges_ExplicitInterfaceImpl_EmitsImplementsEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IRepo { void Save(); }
public class SqlRepo : IRepo { void IRepo.Save() { } }");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        // Explicit impls: Roslyn names them "App.IRepo.Save" in ContainingType=SqlRepo
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("SqlRepo") && e.SourceId.Contains("Save")
            && e.TargetId.Contains("IRepo") && e.TargetId.Contains("Save"));
    }

    [Fact]
    public void ExtractEdges_InheritedImpl_OnlyEmitsForDeclaringType()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IRepo { void Save(); }
public class Base : IRepo { public void Save() { } }
public class Derived : Base { }");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        // Base.Save → IRepo.Save: yes
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("Base.Save") && e.TargetId.Contains("IRepo.Save"));
        // Derived.Save → IRepo.Save: no (inherited, not redeclared)
        Assert.DoesNotContain(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("Derived"));
    }

    [Fact]
    public void ExtractEdges_TransitiveInterface_EmitsImplementsForAll()
    {
        var (model, root) = Compile(@"
namespace App;
public interface IBase { void M(); }
public interface IChild : IBase { void N(); }
public class Impl : IChild { public void M() { } public void N() { } }");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        // Impl.M → IBase.M (transitive)
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("Impl.M") && e.TargetId.Contains("IBase.M"));
        // Impl.N → IChild.N (direct)
        Assert.Contains(edges, e => e.Kind == EdgeKind.Implements
            && e.SourceId.Contains("Impl.N") && e.TargetId.Contains("IChild.N"));
    }

    // ──────────────────────────────────────────────────────────────
    // BUG-005: Symbol-level member access + field reads
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_PropertyAccess_EmitsSymbolLevelReferencesEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Config { public string Name { get; set; } }
public class Service
{
    public void Run(Config c) { var x = c.Name; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId == "method:App.Service.Run(App.Config)"
            && e.TargetId == "property:App.Config.Name");
    }

    [Fact]
    public void ExtractEdges_FieldAccess_ViaMemberAccess_EmitsSymbolLevelEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Data { public int Count; }
public class Reader
{
    public int Get(Data d) { return d.Count; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Reader.Get")
            && e.TargetId == "field:App.Data.Count");
    }

    [Fact]
    public void ExtractEdges_BareFieldIdentifier_EmitsReferencesEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Service
{
    private int _count;
    public int Get() { return _count; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service.Get")
            && e.TargetId == "field:App.Service._count");
    }

    [Fact]
    public void ExtractEdges_PropertyAccess_StillEmitsTypeLevelEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Config { public string Name { get; set; } }
public class Service
{
    public void Run(Config c) { var x = c.Name; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        // Type-level edge still present (regression guard)
        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId == "type:App.Service"
            && e.TargetId == "type:App.Config");
    }

    // ──────────────────────────────────────────────────────────────
    // BUG-006: Null-conditional property access
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_NullConditionalPropertyAccess_EmitsSymbolLevelEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Logger { public string Level { get; set; } }
public class Service
{
    private Logger? _log;
    public string? GetLevel() { return _log?.Level; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Service.GetLevel")
            && e.TargetId == "property:App.Logger.Level");
    }

    [Fact]
    public void ExtractEdges_NullConditionalPropertyAccess_EmitsTypeLevelEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Logger { public string Level { get; set; } }
public class Service
{
    private Logger? _log;
    public string? GetLevel() { return _log?.Level; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId == "type:App.Service"
            && e.TargetId == "type:App.Logger");
    }

    // ──────────────────────────────────────────────────────────────
    // Phase 4: Method-group references
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_MethodGroupInLazy_EmitsCallsEdge()
    {
        // Method-group reference inside a method body (not a static field initializer,
        // because field initializers have no containing method for the edge source).
        var (model, root) = Compile(@"
namespace App;
public class Loader
{
    private string Load() { return ""x""; }
    public System.Lazy<string> GetLazy() { return new System.Lazy<string>(Load); }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Loader.GetLazy")
            && e.TargetId.Contains("Loader.Load"));
    }

    [Fact]
    public void ExtractEdges_MethodGroupInTimerCallback_EmitsCallsEdge()
    {
        var (model, root) = Compile(@"
namespace App;
public class Monitor
{
    private System.Threading.Timer _timer;
    public Monitor() { _timer = new System.Threading.Timer(Tick); }
    private void Tick(object? state) { }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.TargetId.Contains("Monitor.Tick"));
    }

    // ──────────────────────────────────────────────────────────────
    // Lambda context attribution
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_CallInsideLambda_AttributedToEnclosingMethod()
    {
        var (model, root) = Compile(@"
namespace App;
public class Analyzer
{
    public void Run()
    {
        System.Func<int, int> fn = x => Transform(x);
    }
    private static int Transform(int x) { return x * 2; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Analyzer.Run")
            && e.TargetId.Contains("Analyzer.Transform"));
    }

    [Fact]
    public void ExtractEdges_MethodGroupAsDelegate_AttributedToEnclosingMethod()
    {
        var (model, root) = Compile(@"
namespace App;
public class Processor
{
    public void Run()
    {
        System.Func<string, string> fn = Format;
    }
    private static string Format(string s) { return s; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Processor.Run")
            && e.TargetId.Contains("Processor.Format"));
    }

    // ──────────────────────────────────────────────────────────────
    // Constructor Calls edges — find_references on .ctor works
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_NewExpression_EmitsCallsEdgeToCtor()
    {
        // `new Foo(...)` should emit both a type-level References edge (existing)
        // AND a method-level Calls edge to the .ctor, so find_references on the
        // ctor finds the construction site.
        var (model, root) = Compile(@"
namespace App;
public class Foo { public Foo(int x) { } }
public class Caller
{
    public void Build() { var f = new Foo(42); }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId.Contains("Caller.Build")
            && e.TargetId == "type:App.Foo");

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Caller.Build")
            && e.TargetId.Contains("Foo..ctor"));
    }

    [Fact]
    public void ExtractEdges_TargetTypedNew_EmitsCallsEdgeToCtor()
    {
        // C# 9 target-typed new() — ImplicitObjectCreationExpressionSyntax.
        var (model, root) = Compile(@"
namespace App;
public class Foo { public Foo() { } }
public class Caller
{
    public Foo MakeFoo() { Foo f = new(); return f; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId.Contains("Caller.MakeFoo")
            && e.TargetId.Contains("Foo..ctor"));
    }

    // ──────────────────────────────────────────────────────────────
    // Field initializer context — `.cctor` / `.ctor` attribution
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_StaticFieldInitializerMethodGroup_AttributedToCctor()
    {
        // Closes the v0.6.4 "no containing method exists" gap:
        // `static Lazy<T> _x = new(Load)` — `Load` must get an incoming edge so the
        // dead-code analyzer does not flag it.
        var (model, root) = Compile(@"
namespace App;
public class Loader
{
    private static readonly System.Lazy<string> _cache = new System.Lazy<string>(Load);
    private static string Load() { return ""x""; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.TargetId.Contains("Loader.Load"));
    }

    [Fact]
    public void ExtractEdges_InstanceFieldInitializerMethodGroup_AttributedToInstanceCtor()
    {
        var (model, root) = Compile(@"
namespace App;
public class Handler
{
    private readonly System.Action _run = Execute;
    public Handler() { }
    private static void Execute() { }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.TargetId.Contains("Handler.Execute"));
    }

    [Fact]
    public void ExtractEdges_StaticFieldInitializerNewExpression_EmitsCtorCallFromCctor()
    {
        // `static Foo _x = new Foo();` — ctor call inside static field initializer.
        // Caller = synthesized .cctor.
        var (model, root) = Compile(@"
namespace App;
public class Foo { public Foo() { } }
public class Host
{
    private static readonly Foo _instance = new Foo();
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.TargetId.Contains("Foo..ctor"));
    }

    // ──────────────────────────────────────────────────────────────
    // Accessor body context — references inside property getters
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_PropertyGetterBody_EmitsEdgeFromProperty()
    {
        // Closes the accessor-context gap: a field read inside `get { return _x; }` must
        // produce an incoming edge on the field so dead-code analysis sees it as alive.
        // The edge source is the property id (the accessor method is not a graph symbol).
        var (model, root) = Compile(@"
namespace App;
public class Config
{
    private static readonly string[] _names = { ""a"" };
    public static string[] Names { get { return _names; } }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId == "property:App.Config.Names"
            && e.TargetId == "field:App.Config._names");
    }

    [Fact]
    public void ExtractEdges_ExpressionBodiedProperty_EmitsEdgeFromProperty()
    {
        // Expression-bodied property has no AccessorDeclarationSyntax — it lands directly
        // on PropertyDeclarationSyntax with ExpressionBody != null.
        var (model, root) = Compile(@"
namespace App;
public class Config
{
    private static readonly string[] _names = { ""a"" };
    public static string[] Names => _names;
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.References
            && e.SourceId == "property:App.Config.Names"
            && e.TargetId == "field:App.Config._names");
    }

    [Fact]
    public void ExtractEdges_PropertyGetterCallingMethod_EmitsCallsEdgeFromProperty()
    {
        // Method call inside a property getter body — edge source is the property id,
        // edge target is the called method.
        var (model, root) = Compile(@"
namespace App;
public class Service
{
    public int Count => Compute();
    private int Compute() { return 7; }
}");

        var edges = new RoslynEdgeExtractor().Extract(model, root);

        Assert.Contains(edges, e => e.Kind == EdgeKind.Calls
            && e.SourceId == "property:App.Service.Count"
            && e.TargetId.Contains("Service.Compute"));
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
    /// Compile with nullable reference types ENABLED at the compilation
    /// level (the same mode Lifeblood's own csproj uses via
    /// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c>). This is the only
    /// way to reproduce canonical-ID drift caused by nullability flowing
    /// through type display strings.
    /// </summary>
    private static (SemanticModel model, SyntaxNode root) CompileWithNullableEnabled(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { tree }, BclRefs(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var model = compilation.GetSemanticModel(tree);
        return (model, tree.GetRoot());
    }

    // ─────────────────────────────────────────────────────────────────
    // Canonical-ID stability across the symbol / edge extractor boundary
    // when nullable reference types are enabled. Regression test for the
    // dead-code / find-references false-zero that showed up during the
    // v0.6.3 release checkup: a private method with a `List<T>?`
    // parameter called from a sibling method on the same class produced
    // zero incoming Calls edges because the symbol extractor and the
    // edge extractor rendered the parameter type differently.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEdges_MethodCall_NullableGenericParameter_SameClass_ProducesCallsEdge()
    {
        var (model, root) = CompileWithNullableEnabled(@"
#nullable enable
using System.Collections.Generic;
namespace App;
public class Builder
{
    public void ProcessInOrder(List<int>? skipped)
    {
        CreateCompilation(skipped);
    }

    private int CreateCompilation(List<int>? skippedCollector) => 0;
}");

        var symbolExtractor = new RoslynSymbolExtractor();
        var symbols = symbolExtractor.Extract(model, root, "Builder.cs", "file:Builder.cs");

        var edgeExtractor = new RoslynEdgeExtractor();
        var edges = edgeExtractor.Extract(model, root);

        // Definition-side canonical id built by the symbol extractor.
        var defMethod = symbols.FirstOrDefault(s =>
            s.Kind == DomainSymbolKind.Method && s.Name == "CreateCompilation");
        Assert.NotNull(defMethod);
        var defId = defMethod!.Id;

        // Call-side canonical id built by the edge extractor. The Calls
        // edge's TargetId MUST equal the definition's canonical id, or
        // every downstream graph walk (dead_code, find_references,
        // dependants, blast_radius, file_impact) silently produces wrong
        // results for methods that have nullable reference parameters.
        var callsEdge = edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Calls &&
            e.SourceId.Contains("ProcessInOrder", System.StringComparison.Ordinal));

        Assert.True(callsEdge != null,
            $"No Calls edge from ProcessInOrder found at all. Definition id was: {defId}");
        Assert.True(callsEdge!.TargetId == defId,
            $"Calls edge target id drifted from the definition id.\n" +
            $"  defId    = {defId}\n" +
            $"  targetId = {callsEdge.TargetId}");
    }

    [Fact]
    public void ExtractEdges_MethodCall_ComplexSignature_MatchesRealProcessInOrder()
    {
        // Reproduces the v0.6.3 release-checkup dogfood bug as faithfully
        // as possible: ProcessInOrder has FIVE parameters including an
        // array, a source type from another namespace, a nested delegate
        // type, an Action<T,T,T>?, and a List<T>?; CreateCompilation has
        // MetadataReference[] and List<T>?. Against the real Lifeblood
        // workspace, the Calls edge from ProcessInOrder to CreateCompilation
        // was missing from the graph, which cascaded into find_references
        // / dependants / dead_code all returning wrong results for both
        // methods.
        var (model, root) = CompileWithNullableEnabled(@"
#nullable enable
using System;
using System.Collections.Generic;

namespace Lifeblood.Application.Ports.Left;
public sealed class ModuleInfo { public string Name = """"; }
public sealed class AnalysisConfig { public bool RetainCompilations; }

namespace Lifeblood.Domain.Results;
public sealed class SkippedFile { public string FilePath = """"; }

namespace Microsoft.CodeAnalysis;
public class MetadataReference { }

namespace Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using SysList = System.Collections.Generic.List<Lifeblood.Domain.Results.SkippedFile>;

public sealed class ModuleCompilationBuilder
{
    internal delegate void CompilationProcessor(ModuleInfo module, MetadataReference compilation);

    public Dictionary<string, MetadataReference>? ProcessInOrder(
        ModuleInfo[] modules,
        string projectRoot,
        AnalysisConfig config,
        CompilationProcessor processor,
        Action<string, int, int>? onModuleProgress = null,
        List<SkippedFile>? skippedCollector = null)
    {
        var m = modules[0];
        var depRefs = new MetadataReference[0];
        var compilation = CreateCompilation(m, projectRoot, config, depRefs, skippedCollector);
        return null;
    }

    private MetadataReference? CreateCompilation(
        ModuleInfo module, string projectRoot, AnalysisConfig config,
        MetadataReference[] dependencyRefs,
        List<SkippedFile>? skippedCollector) => null;
}");

        var symbolExtractor = new RoslynSymbolExtractor();
        var symbols = symbolExtractor.Extract(model, root, "Builder.cs", "file:Builder.cs");

        var edgeExtractor = new RoslynEdgeExtractor();
        var edges = edgeExtractor.Extract(model, root);

        var defMethod = symbols.FirstOrDefault(s =>
            s.Kind == DomainSymbolKind.Method && s.Name == "CreateCompilation");
        Assert.True(defMethod != null,
            "CreateCompilation symbol was not extracted at all. Symbols: " +
            string.Join(", ", symbols.Where(s => s.Kind == DomainSymbolKind.Method).Select(s => s.Id)));
        var defId = defMethod!.Id;

        var callsEdge = edges.FirstOrDefault(e =>
            e.Kind == EdgeKind.Calls &&
            e.SourceId.Contains("ProcessInOrder", System.StringComparison.Ordinal) &&
            e.TargetId.Contains("CreateCompilation", System.StringComparison.Ordinal));

        Assert.True(callsEdge != null,
            "No Calls edge from ProcessInOrder to CreateCompilation was emitted.\n" +
            "Definition id was: " + defId + "\n" +
            "All Calls edges out of ProcessInOrder:\n  " +
            string.Join("\n  ", edges
                .Where(e => e.Kind == EdgeKind.Calls && e.SourceId.Contains("ProcessInOrder", System.StringComparison.Ordinal))
                .Select(e => e.TargetId)));

        Assert.True(callsEdge!.TargetId == defId,
            "Calls edge target id drifted from the definition id.\n" +
            "  defId    = " + defId + "\n" +
            "  targetId = " + callsEdge.TargetId);
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
