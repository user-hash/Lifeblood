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

    private static (SemanticModel model, SyntaxNode root) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        // Also add System.Runtime for netcore
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refList = new List<MetadataReference>(refs);
        if (runtimeDir != null)
        {
            var sr = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(sr)) refList.Add(MetadataReference.CreateFromFile(sr));
        }

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { tree }, refList,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        return (model, tree.GetRoot());
    }
}
