using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Lifeblood.Tests;

public class VersionedCanonicalSymbolTests
{
    private const CanonicalSymbolIdVersion V2 = CanonicalSymbolIdVersion.AssemblyQualifiedV2;

    [Fact]
    public void Version2_GraphResolverReportsLegacyAmbiguityAndUsesRealMemberOwners()
    {
        using var project = new ProjectFixture(CollisionSource);
        using var session = new GraphSession(new PhysicalFileSystem(), canonicalIdVersion: V2);
        _ = session.Load(project.Root, graphPath: null, rulesPath: null);
        var graph = session.Graph!;
        var resolver = new LifebloodSymbolResolver(new CSharpUserInputCanonicalizer());
        foreach (var legacy in new[] { "type:App.Box", "method:App.C.M(int)", "method:App.C.G()", "method:App.C.op_Implicit(App.C)" })
        {
            var result = resolver.Resolve(graph, legacy);
            Assert.Equal(ResolveOutcome.AmbiguousCanonicalId, result.Outcome);
            Assert.Null(result.CanonicalId);
            Assert.Equal(2, result.Candidates.Length);
            Assert.All(result.Candidates, id => Assert.Equal(id, resolver.Resolve(graph, id).CanonicalId));
        }
        var truncated = resolver.Resolve(graph, "method:App.C.M");
        Assert.Equal(ResolveOutcome.AmbiguousMethodOverload, truncated.Outcome);
        Assert.Equal(2, truncated.Overloads.Length);
        Assert.All(truncated.Overloads, o => Assert.Equal("int", o.ParamDisplay));
        var type = graph.Symbols.Single(s => s.Kind == Lifeblood.Domain.Graph.SymbolKind.Type && s.QualifiedName == "App.C");
        var members = resolver.ResolveMember(graph, type.Id, "M", new[] { "int" });
        Assert.Equal(ResolveMemberOutcome.MultipleMatches, members.Outcome);
        Assert.Equal(2, members.Members.Length);
        Assert.All(members.Members, m => Assert.Equal("int", m.ParamDisplay));
        Assert.Equal(type.Id, resolver.ResolveMember(graph, "App.C", "G").ResolvedTypeId);
        Assert.Equal(ResolveMemberOutcome.AmbiguousContainingType, resolver.ResolveMember(graph, "type:App.Box", "X").Outcome);
        var run = graph.Symbols.Single(s => s.QualifiedName == "App.Use.Run");
        Assert.Equal(run.Id, resolver.Resolve(graph, "method:Old.Use.Run()").CanonicalId);
        Assert.Equal(2, resolver.ResolveShortName(graph, "Box").Length);
        Assert.Contains(resolver.ResolveShortName(graph, run.Id), m => m.CanonicalId == run.Id);
        Assert.Contains(resolver.SuggestNearMatches(graph, type.Id), m => m.CanonicalId == type.Id);
        Assert.Empty(resolver.ResolveShortName(graph, run.Id + "missing"));
        var missing = resolver.Resolve(graph, run.Id + "missing");
        Assert.Equal(ResolveOutcome.NotFound, missing.Outcome);
        Assert.Empty(missing.Candidates);
        Assert.Null(missing.CanonicalId);
    }

    [Fact]
    public void Version2_PredecessorResolutionKeepsPartialDeclarationFilesAndExactNamespacePreference()
    {
        using var project = new ProjectFixture("namespace App; public partial class P { public void A() {} }");
        System.IO.File.WriteAllText(System.IO.Path.Combine(project.Root, "Other.cs"), "namespace App { public partial class P { public void B() {} } } namespace Other { public class P {} }");
        using var session = new GraphSession(new PhysicalFileSystem(), canonicalIdVersion: V2);
        _ = session.Load(project.Root, graphPath: null, rulesPath: null);
        var resolver = new LifebloodSymbolResolver(new CSharpUserInputCanonicalizer());
        Assert.DoesNotContain(session.CompilationHost!.GetDiagnostics(), d => d.Severity == Lifeblood.Domain.Results.DiagnosticSeverity.Error);
        var result = resolver.Resolve(session.Graph!, "type:App.P");
        Assert.Equal(ResolveOutcome.ExactMatch, result.Outcome);
        Assert.Equal(2, result.DeclarationFilePaths.Length);
        Assert.Equal("App.P", result.Symbol!.QualifiedName);
        Assert.Null(session.Graph!.GetSymbol("type:App.P"));
        Assert.Single(session.Graph.FindByPredecessorId("type:App.P"));
        Assert.Equal(2, resolver.ResolveShortName(session.Graph, "P").Length);
    }

    [Fact]
    public void LegacyLookup_RejectsTheFourLegalCollisionPairsBeforeReferencesOrRename()
    {
        var compilation = Compile("Collisions", CollisionSource);
        var compilations = new Dictionary<string, CSharpCompilation> { ["Collisions"] = compilation };
        using var host = new RoslynCompilationHost(compilations, idVersion: V2);
        using var refactoring = new RoslynWorkspaceRefactoring(compilations);
        foreach (var id in new[] { "type:App.Box", "method:App.C.M(int)", "method:App.C.G()", "method:App.C.op_Implicit(App.C)" })
        {
            Assert.Null(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(id)));
            Assert.Null(host.FindDefinition(id));
            Assert.Empty(host.FindReferences(id));
            Assert.Empty(refactoring.Rename(id, "Wrong"));
        }
    }

    [Fact]
    public void LegacyLookup_RejectsDistinctAssemblyOwnersButCollapsesMetadataReplicas()
    {
        var first = Compile("First", "namespace Shared; public class C { public void M() {} }");
        var second = Compile("Second", "namespace Shared; public class C { public void M() {} }");
        var consumer = Compile("Consumer", "", Emit(first), Emit(second));
        using var manager = new RoslynWorkspaceManager(new Dictionary<string, CSharpCompilation> {
            ["Consumer"] = consumer, ["First"] = first, ["Second"] = second,
        });
        Assert.Null(manager.ResolveSymbol("method:Shared.C.M()", useStandaloneCompilations: true));
        Assert.Null(RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId("type:Shared.C")));
        var declaration = first.GetTypeByMetadataName("Shared.C")!.GetMembers("M").Single();
        var exact = manager.ResolveSymbol(CanonicalSymbolFormat.BuildSymbolId(declaration, V2));
        Assert.NotNull(exact);
        Assert.Equal("First", exact!.ContainingAssembly.Name);
        Assert.Contains(exact.Locations, l => l.IsInSource);
        using var replicas = new RoslynWorkspaceManager(new Dictionary<string, CSharpCompilation> {
            ["FirstConsumer"] = Compile("FirstConsumer", "", Emit(first)), ["First"] = first,
        });
        var unique = replicas.ResolveSymbol("method:Shared.C.M()");
        Assert.NotNull(unique);
        Assert.Contains(unique!.Locations, l => l.IsInSource);
    }

    [Fact]
    public void LegacyLookup_RejectsWrongSingleSignatureAndMalformedInputButAcceptsUniqueTruncation()
    {
        var compilation = Compile("Unique", "public class C { public void M(int x) {} public int this[System.DateTime x] => 1; }");
        foreach (var id in new[] { "method:C.M(string)", "method:C.M(", "method:C.M(int)extra", "method:C.get_Item" })
            Assert.Null(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(id)));
        Assert.NotNull(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId("method:C.M")));
        Assert.NotNull(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId("property:C.this[System.DateTime]")));
    }

    [Fact]
    public void Version2_UnityReachabilityFollowsCanonicalBaseEdgeAcrossDuplicateNames()
    {
        var compilation = Compile("UnityApp", """
            namespace UnityEngine { public class MonoBehaviour {} }
            namespace App {
                public interface IMarker {}
                public class Character : UnityEngine.MonoBehaviour {}
                public class Enemy : Character, IMarker { private void Update() {} }
            }
            """);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var symbols = new RoslynSymbolExtractor(V2).Extract(model, tree.GetRoot(), "App.cs", "file:App.cs");
        foreach (var symbol in symbols)
            ((Dictionary<string, string>)symbol.Properties).Remove(SymbolPropertyKeys.BaseTypeChain);
        var builder = new GraphBuilder();
        foreach (var symbol in symbols) builder.AddSymbol(symbol);
        foreach (var edge in new RoslynEdgeExtractor(V2).Extract(model, tree.GetRoot())) builder.AddEdge(edge);
        var other = Compile("OtherAssembly", "namespace App { public class Character {} }");
        var otherTree = other.SyntaxTrees.Single();
        foreach (var symbol in new RoslynSymbolExtractor(V2).Extract(other.GetSemanticModel(otherTree), otherTree.GetRoot(), "Other.cs", "file:Other.cs"))
            builder.AddSymbol(symbol);
        var graph = builder.Build();
        Assert.Equal(2, graph.FindByShortName("Character").Count);
        var update = symbols.Single(s => s.Name == "Update");
        Assert.True(new UnityReachabilityAdapter().IsRuntimeReachable(graph, update, out _));
    }

    private const string CollisionSource = """
        namespace App;
        public class Box {}
        public class Box<T> {}
        public class C {
            public void M(int x) {}
            public void M(ref int x) {}
            public void G<T>() {}
            public void G<T,U>() {}
            public static implicit operator int(C value) => 1;
            public static implicit operator string(C value) => "";
        }
        public class Use {
            public void Run() {
                var c = new C(); int x = 0;
                c.M(0); c.M(ref x); c.G<int>(); c.G<int,string>();
                int number = c; string text = c;
            }
        }
        """;

    [Fact]
    public void Version2_PublicationIdentityIncludesTheDeclarationPolicy()
    {
        using var project = new ProjectFixture(CollisionSource);
        using var legacy = new GraphSession(new PhysicalFileSystem());
        using var current = new GraphSession(new PhysicalFileSystem(), canonicalIdVersion: V2);
        _ = legacy.Load(project.Root, graphPath: null, rulesPath: null, readOnly: true);
        _ = current.Load(project.Root, graphPath: null, rulesPath: null, readOnly: true);
        var oldIdentity = legacy.CurrentSnapshot.Identity!;
        var newIdentity = current.CurrentSnapshot.Identity!;
        Assert.Equal(oldIdentity.Workspace, newIdentity.Workspace);
        Assert.Equal(oldIdentity.Source, newIdentity.Source);
        Assert.Equal(oldIdentity.Spec.RetentionMode, newIdentity.Spec.RetentionMode);
        Assert.Null(oldIdentity.Spec.DeclarationIdentityPolicy);
        Assert.NotNull(newIdentity.Spec.DeclarationIdentityPolicy);
        Assert.NotEqual(oldIdentity.AnalysisKey, newIdentity.AnalysisKey);
        Assert.Equal("current", current.CheckSnapshotDrift(new[] { current.CurrentSnapshot }).Values.Single().Status);
        Assert.Equal(newIdentity.Spec.DeclarationIdentityPolicy, Lifeblood.Domain.Workspaces.WorkspaceAnalysisDescriptor.From(newIdentity).DeclarationIdentityPolicy);
    }

    [Fact]
    public void Version2_RefactoringWorkspacePreservesAssemblyAndParseOptions()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            #if KEPT
            public class C { public void M() {} public void Run() { M(); } }
            #else
            public class Other {}
            #endif
            """, options: CSharpParseOptions.Default.WithPreprocessorSymbols("KEPT"), path: "Options.cs");
        var compilation = CSharpCompilation.Create("ActualAssembly", new[] { tree }, BclReferenceLoader.References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var id = CanonicalSymbolFormat.BuildSymbolId(compilation.GetTypeByMetadataName("C")!.GetMembers("M").Single(), V2);
        using var refactoring = new RoslynWorkspaceRefactoring(new Dictionary<string, CSharpCompilation> { ["ModuleLabel"] = compilation });
        Assert.Equal(2, refactoring.Rename(id, "Changed").Length);
    }

    [Fact]
    public void Version2_GraphSession_PreservesDeclarationsEdgesAndExactReferenceTargets()
    {
        using var project = new ProjectFixture(CollisionSource);
        using var session = new GraphSession(new PhysicalFileSystem(), canonicalIdVersion: V2);
        _ = session.Load(project.Root, graphPath: null, rulesPath: null);
        var graph = session.Graph!;
        var host = session.CompilationHost!;
        Assert.DoesNotContain(host.GetDiagnostics(), d => d.Severity == Lifeblood.Domain.Results.DiagnosticSeverity.Error);
        foreach (var legacy in new[] { "type:App.Box", "method:App.C.M(int)", "method:App.C.G()", "method:App.C.op_Implicit(App.C)" })
            Assert.Equal(2, graph.Symbols.Count(s => s.Properties.TryGetValue(SymbolPropertyKeys.LegacyCanonicalId, out var id) && id == legacy));
        Assert.All(graph.Symbols, s => Assert.DoesNotContain("v2|", s.QualifiedName));
        var targets = graph.Symbols.Where(s => s.Kind == Lifeblood.Domain.Graph.SymbolKind.Method && s.Name is "M" or "G" or "op_Implicit").ToArray();
        Assert.Equal(6, targets.Length);
        var caller = graph.Symbols.Single(s => s.QualifiedName == "App.Use.Run");
        var report = host.FindReferences(new FindReferencesRequest { SymbolIds = targets.Select(s => s.Id).ToArray() },
            new FindReferencesOptions { IncludeDeclarations = true });
        Assert.Equal(1, report.Work.ConversionScanCount);
        foreach (var group in report.Symbols)
        {
            Assert.True(group.Resolved, group.SymbolId);
            Assert.Single(group.Locations, l => l.Kind == ReferenceKind.Declaration);
            var usage = Assert.Single(group.Locations, l => l.Kind == ReferenceKind.Usage);
            Assert.Equal(caller.Id, usage.ContainingSymbolId);
        }
        foreach (var target in targets.Where(s => s.Name is "M" or "G"))
            Assert.Contains(graph.Edges, e => e.SourceId == caller.Id && e.TargetId == target.Id && e.Kind == EdgeKind.Calls);
        var method = targets.Single(s => s.Name == "M" && host.FindReferences(s.Id).Single().SpanText.Contains("M(0)", StringComparison.Ordinal));
        var definition = host.FindDefinition(method.Id);
        Assert.NotNull(definition);
        Assert.Equal(method.Id, definition!.SymbolId);
        var edits = session.Refactoring!.Rename(method.Id, "Changed");
        Assert.Equal(2, edits.Length);
        Assert.All(edits, e => Assert.Equal("Changed", e.NewText));
    }

    [Fact]
    public void Version2_GraphSession_NoopAndIncrementalCandidatesKeepTheFormat()
    {
        using var project = new ProjectFixture(CollisionSource);
        using var session = new GraphSession(new PhysicalFileSystem(), canonicalIdVersion: V2);
        _ = session.Load(project.Root, graphPath: null, rulesPath: null);
        var ids = session.Graph!.Symbols.Select(s => s.Id).OrderBy(id => id).ToArray();
        var before = session.CurrentSnapshot;
        _ = session.Load(project.Root, graphPath: null, rulesPath: null, incremental: true);
        Assert.Same(before, session.CurrentSnapshot);
        File.WriteAllText(project.SourcePath, CollisionSource.Replace("c.M(0);", "c.M(0); c.M(1);", StringComparison.Ordinal));
        _ = session.Load(project.Root, graphPath: null, rulesPath: null, incremental: true);
        Assert.NotSame(before, session.CurrentSnapshot);
        Assert.Equal(ids, session.Graph!.Symbols.Select(s => s.Id).OrderBy(id => id));
        var methods = session.Graph.Symbols.Where(s => s.Name == "M").ToArray();
        Assert.Equal(new[] { 1, 2 }, methods.Select(m => session.CompilationHost!.FindReferences(m.Id).Length).OrderBy(n => n));
    }

    [Fact]
    public void Version2_Host_CrossAssemblyReferencesAndRenamePreferSource()
    {
        var library = Compile("Library", "namespace App; public class C { public void M(int x) {} public void M(ref int x) {} }");
        var consumer = Compile("Consumer", "namespace App; public class Use { public void Run(C c) { int x=0; c.M(0); c.M(ref x); } }", Emit(library));
        var compilations = new Dictionary<string, CSharpCompilation> { ["Consumer"] = consumer, ["Library"] = library };
        var dependencies = new Dictionary<string, string[]> { ["Consumer"] = new[] { "Library" }, ["Library"] = Array.Empty<string>() };
        using var host = new RoslynCompilationHost(compilations, dependencies, idVersion: V2);
        var type = library.GetTypeByMetadataName("App.C")!;
        foreach (var method in type.GetMembers("M"))
        {
            var id = CanonicalSymbolFormat.BuildSymbolId(method, V2);
            var reference = Assert.Single(host.FindReferences(id));
            Assert.EndsWith("Consumer.cs", reference.FilePath);
            var definition = host.FindDefinition(id);
            Assert.NotNull(definition);
            Assert.EndsWith("Library.cs", definition!.FilePath);
        }
        using var refactoring = new RoslynWorkspaceRefactoring(compilations, dependencies);
        var first = type.GetMembers("M").OfType<IMethodSymbol>().Single(m => m.Parameters[0].RefKind == RefKind.None);
        var edits = refactoring.Rename(CanonicalSymbolFormat.BuildMethodId(first, V2), "Changed");
        Assert.Equal(2, edits.Length);
        Assert.Contains(edits, e => e.FilePath.EndsWith("Library.cs", StringComparison.Ordinal));
        Assert.Contains(edits, e => e.FilePath.EndsWith("Consumer.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Version2_RetainedAndEphemeralFactsUseGraphIds()
    {
        const string source = """
            public class C {
                public void M(int x) {}
                public void M(ref int x) {}
                public void Run() {
                    // RECEIPT
                    int x = 0;
            #if SECOND
                    M(ref x);
            #else
                    M(0);
            #endif
                    void Local() { /* RECEIPT */ M(0); }
                    Local();
                }
            }
            """;
        using var project = new ProjectFixture(source);
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem(), new FixtureProfiles(), V2);
        var graph = analyzer.AnalyzeWorkspace(project.Root, new AnalysisConfig { RetainCompilations = true, DefineProfiles = new[] { "First", "Second" } });
        foreach (var profile in new[] { "First", "Second" })
        {
            var facts = new List<OperationFact>();
            var receipt = analyzer.ScanOperationFacts(new OperationFactQuery { ProfileScope = profile, IncludeKinds = new[] { OperationFactKind.Call } }, fact => { facts.Add(fact); return true; });
            Assert.Equal(OperationFactScanStatus.Completed, receipt.Status);
            Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
            Assert.Equal(profile == "First" ? OperationFactExecutionMode.RetainedCompilation : OperationFactExecutionMode.EphemeralProfileCompilation, receipt.ExecutionMode);
            foreach (var fact in facts.Where(f => f.TargetSymbolId?.StartsWith("method:", StringComparison.Ordinal) == true))
                Assert.NotNull(graph.GetSymbol(fact.TargetSymbolId!));
            Assert.Contains(facts, f => f.TargetSymbolId?.StartsWith("unknown:v2|", StringComparison.Ordinal) == true);
            Assert.All(facts, f => Assert.NotNull(graph.GetSymbol(f.ContainingSymbolId!)));
        }
        var evidence = new List<SourceEvidenceFact>();
        analyzer.ScanSourceEvidence(new SourceEvidenceQuery { SearchTerms = new[] { "RECEIPT" } }, fact => { evidence.Add(fact); return true; });
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, f => Assert.Equal(graph.Symbols.Single(s => s.Name == "Run").Id, f.ContainingSymbolId));
    }

    [Fact]
    public void Version2_UnresolvedAndCompilerCreatedSymbolsCannotAliasDeclarations()
    {
        const string source = "public class C { public void M(Missing x) {} public void Run() { var value = new { Number=1 }; _ = value.Number; } }";
        var compilation = CSharpCompilation.Create("Errors", new[] { CSharpSyntaxTree.ParseText(source, path: "Errors.cs") },
            BclReferenceLoader.References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var tree = compilation.SyntaxTrees.Single();
        var symbols = new RoslynSymbolExtractor(V2).Extract(compilation.GetSemanticModel(tree), tree.GetRoot(), "Errors.cs", "file:Errors.cs");
        var method = symbols.Single(s => s.Name == "M");
        var resolvedCompilation = Compile("Errors", "public class Missing {} public class C { public void M(Missing x) {} }");
        var resolvedMethod = resolvedCompilation.GetTypeByMetadataName("C")!.GetMembers("M").Single();
        Assert.NotEqual(method.Id, CanonicalSymbolFormat.BuildSymbolId(resolvedMethod, V2));
        Assert.Null(RoslynWorkspaceManager.FindInCompilation(resolvedCompilation, RoslynWorkspaceManager.ParseSymbolId(method.Id)));
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["Errors"] = compilation }, idVersion: V2);
        Assert.NotNull(host.FindDefinition(method.Id));
        var facts = new List<OperationFact>();
        host.ScanOperationFacts(new OperationFactQuery(), f => { facts.Add(f); return true; });
        Assert.Contains(facts, f => f.TargetSymbolId?.StartsWith("unknown:v2|", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Version2_InputCanonicalizationPreservesEncodedDeclarationAddresses()
    {
        var input = "type:v2|Assembly|T%3ASystem.Int32||";
        Assert.Equal(input, new CSharpUserInputCanonicalizer().Canonicalize(input));
    }

    private sealed class FixtureProfiles : IDefineProfileResolver
    {
        public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot) => new[]
        {
            new DefineProfile { Name = "First", AddDefines = Array.Empty<string>(), RemoveDefines = Array.Empty<string>() },
            new DefineProfile { Name = "Second", AddDefines = new[] { "SECOND" }, RemoveDefines = Array.Empty<string>() },
        };
    }

    private sealed class ProjectFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lifeblood-v2-" + Guid.NewGuid().ToString("N"));
        public string SourcePath => Path.Combine(Root, "App.cs");
        public ProjectFixture(string source)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SourcePath, source);
            File.WriteAllText(Path.Combine(Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("out")]
    [InlineData("in")]
    [InlineData("ref readonly")]
    public void Version2_PassingModeIsPartOfIdentity(string mode)
    {
        var body = mode == "out" ? "x = 0;" : "";
        var compilation = Compile("Modes", $"public class C {{ public void M(int x) {{}} public void M({mode} int x) {{ {body} }} }}");
        var methods = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers("M");
        Assert.Equal(2, methods.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        var consumer = Compile("Consumer", "", Emit(compilation));
        foreach (var method in methods)
        {
            AssertRoundTrip(compilation, method);
            var id = CanonicalSymbolFormat.BuildSymbolId(method, V2);
            var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.True(resolved != null, id);
        }
    }

    [Fact]
    public void Version2_EnclosingTypeArityDistinguishesEveryMember()
    {
        var compilation = Compile("Owners", """
            public class Outer<T> { public class Inner { public void M() {} public int X; } }
            public class Outer<T,U> { public class Inner { public void M() {} public int X; } }
            """);
        var symbols = compilation.GlobalNamespace.GetTypeMembers("Outer")
            .SelectMany(o => o.GetTypeMembers("Inner"))
            .SelectMany(i => new ISymbol[] { i, i.GetMembers("M").Single(), i.GetMembers("X").Single() }).ToArray();
        Assert.Equal(3, symbols.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct().Count());
        Assert.Equal(6, symbols.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var symbol in symbols) AssertRoundTrip(compilation, symbol);
    }

    [Fact]
    public void UnknownVersionCannotFallBackToLegacyIdentity()
    {
        var compilation = Compile("Version", "public class C { public int X; public int Value {get;set;} public event System.Action? Changed; public void M() {} }");
        var type = compilation.GlobalNamespace.GetTypeMembers("C").Single();
        foreach (var symbol in type.GetMembers().Append(type).Append(compilation.GlobalNamespace))
            Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalSymbolFormat.BuildSymbolId(symbol, (CanonicalSymbolIdVersion)3));
    }

    [Theory]
    [InlineData("public class C { public void M(int x) {} public void M(ref int x) {} }", "M")]
    [InlineData("public class C { public void M<T>() {} public void M<T,U>() {} }", "M")]
    [InlineData("public class Box {} public class Box<T> {}", "Box")]
    [InlineData("public class C { public static implicit operator int(C x) => 0; public static implicit operator string(C x) => \"\"; }", "op_Implicit")]
    public void Version2_DistinguishesLegalDeclarations_WhileLegacyBytesStayUnchanged(string source, string name)
    {
        var compilation = Compile("Declarations", source);
        var declarations = name == "Box"
            ? compilation.GlobalNamespace.GetTypeMembers(name).Cast<ISymbol>().ToArray()
            : compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers(name).ToArray();
        Assert.Equal(2, declarations.Length);
        Assert.Single(declarations.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct());
        Assert.Equal(2, declarations.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var declaration in declarations) AssertRoundTrip(compilation, declaration);
    }

    [Fact]
    public void Version2_RoundTripsNestedGenericMembers_AndSourceMetadataCopies()
    {
        var source = Compile("Library", """
            using System;
            using System.Collections.Generic;
            namespace App;
            public class Outer<T> {
                public class Inner<U> {
                    public Inner() {}
                    static Inner() {}
                    public T Field = default!;
                    public event Action? Changed;
                    public T this[int key] { get => Field; set => Field = value; }
                    public void M<V>(ref T x, out U y, in V z, List<T> list, int? first, string? second) { y = default!; Changed?.Invoke(); }
                    public void N(int? x) {}
                    public void N(long? x) {}
                    public void Dynamic(dynamic x) {}
                    public void Tuple((int A, string B) value) {}
                }
            }
            """);
        var metadata = Emit(source);
        var consumer = Compile("Consumer", "", metadata);
        var outer = source.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "App").GetTypeMembers("Outer").Single();
        var inner = outer.GetTypeMembers("Inner").Single();
        var symbols = new ISymbol[] { outer, inner }.Concat(inner.GetMembers()
            .Where(s => s is IMethodSymbol or IFieldSymbol or IPropertySymbol or IEventSymbol)).ToArray();
        foreach (var symbol in symbols)
        {
            var normalized = Normalize(symbol);
            AssertRoundTrip(source, normalized);
            var id = CanonicalSymbolFormat.BuildSymbolId(normalized, V2);
            var fromMetadata = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.True(fromMetadata != null, id);
            Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(fromMetadata!, V2));
        }
        Assert.Equal(2, inner.GetMembers("N").Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        var constructed = inner.Construct(source.GetSpecialType(SpecialType.System_String));
        Assert.Equal(CanonicalSymbolFormat.BuildTypeId(inner, V2), CanonicalSymbolFormat.BuildTypeId(constructed, V2));
        Assert.Equal(CanonicalSymbolFormat.BuildFieldId((IFieldSymbol)inner.GetMembers("Field").Single(), V2),
            CanonicalSymbolFormat.BuildFieldId((IFieldSymbol)constructed.GetMembers("Field").Single(), V2));
    }

    [Fact]
    public void Version2_NormalizesPartialConstructedAndReducedMethods()
    {
        var compilation = Compile("Normalize", """
            public partial class C {
                partial void Partial(int x);
                partial void Partial(int x) {}
                public void Generic<T>(T x) {}
            }
            public static class Extensions {
                public static void Extend<T>(this C value, T x) {}
                public static void Call(C value) { value.Extend(1); }
            }
            """);
        var type = compilation.GlobalNamespace.GetTypeMembers("C").Single();
        var partial = (IMethodSymbol)type.GetMembers("Partial").Single();
        Assert.NotNull(partial.PartialImplementationPart);
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(partial, V2), CanonicalSymbolFormat.BuildMethodId(partial.PartialImplementationPart!, V2));
        var generic = (IMethodSymbol)type.GetMembers("Generic").Single();
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(generic, V2),
            CanonicalSymbolFormat.BuildMethodId(generic.Construct(compilation.GetSpecialType(SpecialType.System_Int32)), V2));
        var tree = compilation.SyntaxTrees.Single();
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var reduced = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol!;
        Assert.NotNull(reduced.ReducedFrom);
        Assert.Equal(CanonicalSymbolFormat.BuildMethodId(reduced.ReducedFrom!, V2), CanonicalSymbolFormat.BuildMethodId(reduced, V2));
        AssertRoundTrip(compilation, generic);
        AssertRoundTrip(compilation, partial);
    }

    [Fact]
    public void Version2_DistinguishesAssemblyOwnersAndExternAliasParameterTypes()
    {
        var first = Compile("First", "namespace Shared; public class Item {} public class C { public void M() {} }");
        var second = Compile("Second", "namespace Shared; public class Item {} public class C { public void M() {} }");
        var consumer = Compile("Consumer", """
            extern alias A;
            extern alias B;
            public class Overloads {
                public void M(A::Shared.Item item) {}
                public void M(B::Shared.Item item) {}
            }
            """, Emit(first).WithAliases(new[] { "A" }), Emit(second).WithAliases(new[] { "B" }));
        var overloads = consumer.GlobalNamespace.GetTypeMembers("Overloads").Single().GetMembers("M");
        Assert.Single(overloads.Select(s => CanonicalSymbolFormat.BuildSymbolId(s)).Distinct());
        Assert.Equal(2, overloads.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().Count());
        foreach (var method in overloads) AssertRoundTrip(consumer, method);
        var ownerIds = new HashSet<string>();
        foreach (var compilation in new[] { first, second })
        {
            var owner = compilation.GlobalNamespace.GetNamespaceMembers().Single(n => n.Name == "Shared").GetTypeMembers("C").Single();
            foreach (var symbol in new ISymbol[] { owner, owner.GetMembers("M").Single() })
            {
                var id = CanonicalSymbolFormat.BuildSymbolId(symbol, V2);
                Assert.True(ownerIds.Add(id));
                var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
                Assert.NotNull(resolved);
                Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(resolved!, V2));
            }
        }
    }

    [Fact]
    public void Version2_RoundTripsPointerFunctionPointerAndExplicitInterfaceMembers()
    {
        var compilation = Compile("Unsafe", """
            public interface I { void M(); int Value { get; } }
            public unsafe class C : I {
                void I.M() {}
                int I.Value => 1;
                public void P(int* x) {}
                public void P(long* x) {}
                public void F(delegate*<int, void> x) {}
                public void F(delegate* unmanaged[Cdecl]<int, void> x) {}
            }
            """);
        var members = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers()
            .Where(s => s is IMethodSymbol or IPropertySymbol).Select(Normalize).ToArray();
        foreach (var member in members) AssertRoundTrip(compilation, member);
        var ids = members.Select(s => CanonicalSymbolFormat.BuildSymbolId(s, V2)).Distinct().ToArray();
        Assert.Equal(members.Distinct(SymbolEqualityComparer.Default).Count(), ids.Length);
        var consumer = Compile("Consumer", "", Emit(compilation));
        foreach (var id in ids)
        {
            var resolved = RoslynWorkspaceManager.FindInCompilation(consumer, RoslynWorkspaceManager.ParseSymbolId(id));
            Assert.NotNull(resolved);
            Assert.Equal(id, CanonicalSymbolFormat.BuildSymbolId(resolved!, V2));
        }
    }

    [Fact]
    public void Version2_RejectsMalformedOrInexactIds()
    {
        var compilation = Compile("Exact", "public class C { public void M() {} }");
        var method = compilation.GlobalNamespace.GetTypeMembers("C").Single().GetMembers("M").Single();
        var id = CanonicalSymbolFormat.BuildSymbolId(method, V2);
        foreach (var invalid in new[] { "method:v2|", id + "|extra", id + "wrong", id.Replace("method:", "field:") })
            Assert.Null(RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(invalid)));
    }

    private static ISymbol Normalize(ISymbol symbol)
        => symbol is IMethodSymbol { AssociatedSymbol: { } owner } ? owner : symbol;

    private static void AssertRoundTrip(CSharpCompilation compilation, ISymbol symbol)
    {
        var id = CanonicalSymbolFormat.BuildSymbolId(symbol, V2);
        var resolved = RoslynWorkspaceManager.FindInCompilation(compilation, RoslynWorkspaceManager.ParseSymbolId(id));
        Assert.True(resolved != null, id);
        Assert.True(SymbolEqualityComparer.Default.Equals(symbol, resolved), id);
    }

    private static CSharpCompilation Compile(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source, path: name + ".cs") },
            BclReferenceLoader.References.Value.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                metadataImportOptions: MetadataImportOptions.All));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        return compilation;
    }

    private static PortableExecutableReference Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
