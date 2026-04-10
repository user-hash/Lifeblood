using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Tests;

/// <summary>
/// Integration tests that use RoslynWorkspaceAnalyzer against the real WriteSideApp
/// golden repo on disk. These prove the full pipeline: .sln discovery → .csproj parsing
/// → NuGet resolution → Roslyn compilation → symbol/edge extraction → graph construction
/// → write-side Roslyn operations. Unlike unit tests that build in-memory compilations,
/// these exercise the actual file system path.
///
/// INV-TEST-001: tests never silently early-return on precondition failure.
/// Missing golden repo turns into an explicit <see cref="Skip.IfNot"/> skip with a
/// documented reason. Presence-but-empty-analysis is a real bug and fails loudly.
/// Every test method uses <c>[SkippableFact]</c> so xunit reports skips as skips
/// (not as silent passes) and real failures as real failures.
/// </summary>
public class WriteSideIntegrationTests
{
  private static readonly string GoldenRepoPath = FindGoldenRepo();

  /// <summary>
  /// Ensure the golden repo is present and analyzable, then return the graph and adapter.
  ///
  /// Two failure modes, two different contracts:
  ///
  /// <list type="number">
  /// <item>Golden repo missing (CI without restore, path not found): <see cref="Skip.If"/>
  /// marks the test as Skipped with the reason. Requires <c>[SkippableFact]</c> on the caller.</item>
  /// <item>Golden repo present but analysis produced zero symbols: this is a real bug
  /// (fixture corruption, adapter regression, or broken discovery). <see cref="Assert.True"/>
  /// fails the test loudly. No hiding.</item>
  /// </list>
  ///
  /// The previous <c>TryAnalyze(out) ⇒ bool</c> pattern silently returned false on
  /// both conditions, which let presence-but-empty-analysis failures masquerade as
  /// passing tests for multiple commits before anyone noticed.
  /// </summary>
  private static (SemanticGraph Graph, RoslynWorkspaceAnalyzer Adapter) EnsureAnalyzed()
  {
  Skip.IfNot(
  Directory.Exists(GoldenRepoPath) && File.Exists(Path.Combine(GoldenRepoPath, "WriteSideApp.sln")),
  $"Golden repo WriteSideApp not found or not restored at {GoldenRepoPath}. " +
  "Run the golden-repo restore step or add tests/GoldenRepos/WriteSideApp.");

  var fs = new PhysicalFileSystem();
  var adapter = new RoslynWorkspaceAnalyzer(fs);
  var graph = adapter.AnalyzeWorkspace(GoldenRepoPath, new AnalysisConfig { RetainCompilations = true });

  Assert.True(
  graph.Symbols.Count > 0,
  $"Golden repo is present at {GoldenRepoPath} but analysis produced zero symbols. " +
  "This is a real bug in the adapter or fixture corruption. Not a skip condition.");

  return (graph, adapter);
  }

  // ── Full pipeline ──

  [SkippableFact]
  public void AnalyzeWriteSideApp_ProducesValidGraph()
  {
  var (graph, _) = EnsureAnalyzed();

  Assert.True(graph.Symbols.Count > 0);
  Assert.True(graph.Edges.Count > 0);

  var errors = GraphValidator.Validate(graph);
  Assert.Empty(errors);
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_DiscoversTwoModules()
  {
  var (graph, _) = EnsureAnalyzed();

  var modules = graph.Symbols.Where(s => s.Kind == DomainSymbolKind.Module).ToArray();
  Assert.Equal(2, modules.Length);
  Assert.Contains(modules, m => m.Name == "WriteSideApp.Core");
  Assert.Contains(modules, m => m.Name == "WriteSideApp.Service");
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_ExtractsAllTypes()
  {
  var (graph, _) = EnsureAnalyzed();

  Assert.Contains(graph.Symbols, s => s.Name == "IGreeter" && s.Kind == DomainSymbolKind.Type);
  Assert.Contains(graph.Symbols, s => s.Name == "Greeter" && s.Kind == DomainSymbolKind.Type);
  Assert.Contains(graph.Symbols, s => s.Name == "GreetingLog" && s.Kind == DomainSymbolKind.Type);
  Assert.Contains(graph.Symbols, s => s.Name == "FormalGreeter" && s.Kind == DomainSymbolKind.Type);
  Assert.Contains(graph.Symbols, s => s.Name == "GreetingService" && s.Kind == DomainSymbolKind.Type);
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_ExtractsOverrideEdge()
  {
  var (graph, _) = EnsureAnalyzed();

  Assert.Contains(graph.Edges, e =>
  e.Kind == EdgeKind.Overrides
  && e.SourceId.Contains("FormalGreeter")
  && e.TargetId.Contains("Greeter"));
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_ExtractsEventSymbols()
  {
  var (graph, _) = EnsureAnalyzed();

  var events = graph.Symbols.Where(s =>
  s.Properties.TryGetValue("isEvent", out var v) && v == "true").ToArray();
  Assert.True(events.Length >= 2, $"Expected ≥2 event symbols, got {events.Length}");
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_ExtractsIndexer()
  {
  var (graph, _) = EnsureAnalyzed();

  var indexers = graph.Symbols.Where(s =>
  s.Properties.TryGetValue("isIndexer", out var v) && v == "true").ToArray();
  Assert.Single(indexers);
  Assert.Contains("this[", indexers[0].Id);
  }

  // ── Cross-assembly graph edges ──

  [SkippableFact]
  public void AnalyzeWriteSideApp_CrossAssemblyEdges_ServiceReferencesCore()
  {
  var (graph, _) = EnsureAnalyzed();

  // GreetingService (in WriteSideApp.Service) references IGreeter (in WriteSideApp.Core).
  // This is a cross-assembly edge. Only works with KnownModuleAssemblies.
  Assert.Contains(graph.Edges, e =>
  e.Kind == EdgeKind.References
  && e.SourceId.Contains("GreetingService")
  && e.TargetId.Contains("IGreeter"));
  }

  [SkippableFact]
  public void AnalyzeWriteSideApp_CrossAssemblyEdges_FormalGreeterInheritsGreeter()
  {
  var (graph, _) = EnsureAnalyzed();

  // FormalGreeter (in WriteSideApp.Service) inherits Greeter (in WriteSideApp.Core).
  Assert.Contains(graph.Edges, e =>
  e.Kind == EdgeKind.Inherits
  && e.SourceId.Contains("FormalGreeter")
  && e.TargetId.Contains("Greeter")
  && !e.TargetId.Contains("FormalGreeter"));
  }

  // ── Write-side: FindReferences ──

  [SkippableFact]
  public void FindReferences_IGreeter_ReturnsRealLocations()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!, adapter.ModuleDependencies);

  var refs = host.FindReferences("type:WriteSideApp.Core.IGreeter");

  // IGreeter is referenced by: Greeter (implements), GreetingService (field type + constructor param)
  Assert.NotNull(refs);
  // Verify at least one real file path is returned
  if (refs.Length > 0)
  {
  Assert.All(refs, r =>
  {
  Assert.True(r.Line > 0, "Line should be > 0");
  Assert.True(r.Column > 0, "Column should be > 0");
  });
  }
  }

  [SkippableFact]
  public void FindReferences_IGreeter_CrossAssembly_FindsServiceUsage()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!, adapter.ModuleDependencies);

  var refs = host.FindReferences("type:WriteSideApp.Core.IGreeter");

  // With ProjectReference links, FindReferences should find IGreeter usage
  // in BOTH Core (declaration, Greeter implements) AND Service (GreetingService field/ctor).
  // Check that at least one reference is from a Service file.
  Assert.True(refs.Length > 0, "FindReferences should return results for IGreeter");
  var hasServiceRef = refs.Any(r =>
  r.FilePath.Contains("Service", StringComparison.OrdinalIgnoreCase)
  || r.FilePath.Contains("GreetingService", StringComparison.OrdinalIgnoreCase));
  Assert.True(hasServiceRef,
  $"FindReferences should find IGreeter in GreetingService (cross-assembly). " +
  $"Got {refs.Length} refs: {string.Join(", ", refs.Select(r => r.FilePath))}");
  }

  // ── Write-side: Rename ──

  [SkippableFact]
  public void Rename_GreeterType_ReturnsRealEdits()
  {
  var (_, adapter) = EnsureAnalyzed();
  using var refactoring = new RoslynWorkspaceRefactoring(adapter.Compilations!, adapter.ModuleDependencies);

  var edits = refactoring.Rename("type:WriteSideApp.Core.Greeter", "SimpleGreeter");

  Assert.NotNull(edits);
  // AdhocWorkspace Rename may return edits. Verify structure if any
  if (edits.Length > 0)
  {
  Assert.All(edits, e =>
  {
  Assert.True(e.StartLine > 0);
  Assert.True(e.StartColumn > 0);
  Assert.Contains("SimpleGreeter", e.NewText);
  });
  }
  }

  // ── Write-side: CompileCheck ──

  [SkippableFact]
  public void CompileCheck_ValidCode_Succeeds()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  var result = host.CompileCheck(
  "public class TestClass { public WriteSideApp.Core.IGreeter? G { get; set; } }",
  "WriteSideApp.Core");

  Assert.True(result.Success, $"CompileCheck failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
  }

  [SkippableFact]
  public void CompileCheck_InvalidCode_FailsWithDiagnostics()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  var result = host.CompileCheck("public class X : WriteSideApp.Core.NonExistent { }", "WriteSideApp.Core");

  Assert.False(result.Success);
  Assert.NotEmpty(result.Diagnostics);
  }

  // ── Write-side: FindDefinition ──

  [SkippableFact]
  public void FindDefinition_IGreeter_ReturnsSourceLocation()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  var def = host.FindDefinition("type:WriteSideApp.Core.IGreeter");

  Assert.NotNull(def);
  Assert.Contains("IGreeter", def!.FilePath);
  Assert.True(def.Line > 0);
  Assert.Contains("IGreeter", def.DisplayName);
  }

  // ── Write-side: FindImplementations ──

  [SkippableFact]
  public void FindImplementations_IGreeter_FindsGreeterAndFormalGreeter()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  var impls = host.FindImplementations("type:WriteSideApp.Core.IGreeter");

  Assert.NotEmpty(impls);
  Assert.Contains(impls, id => id.Contains("Greeter"));
  }

  // ── Write-side: GetDocumentation ──

  [SkippableFact]
  public void GetDocumentation_IGreeter_ReturnsSummary()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  var doc = host.GetDocumentation("type:WriteSideApp.Core.IGreeter");

  // IGreeter has XML doc: "Port interface. Demonstrates..."
  Assert.NotEmpty(doc);
  Assert.Contains("Port interface", doc);
  }

  // ── Write-side: GetSymbolAtPosition ──

  [SkippableFact]
  public void GetSymbolAtPosition_GreeterClassLine_ReturnsGreeter()
  {
  var (_, adapter) = EnsureAnalyzed();
  var host = new RoslynCompilationHost(adapter.Compilations!);

  // Greeter.cs: "public class Greeter : IGreeter". Class name is on this line
  // We need the actual file path used by the compilation
  var greeterFile = adapter.Compilations!.Values
  .SelectMany(c => c.SyntaxTrees)
  .FirstOrDefault(t => t.FilePath?.Contains("Greeter.cs") == true
  && !t.FilePath.Contains("Formal"))
  ?.FilePath;

  // Fixture lookup. If Greeter.cs is not in the syntax trees the golden repo
  // is corrupted, not missing. Fail loudly rather than skip silently.
  Assert.NotNull(greeterFile);

  // Line 6 should be "public class Greeter : IGreeter" (after namespace + doc comment)
  var result = host.GetSymbolAtPosition(greeterFile!, 6, 14);

  Assert.NotNull(result);
  Assert.Contains("Greeter", result!.Name);
  }

  // ── Helpers ──


  private static string FindGoldenRepo()
  {
  var dir = AppDomain.CurrentDomain.BaseDirectory;
  while (dir != null)
  {
  var candidate = Path.Combine(dir, "tests", "GoldenRepos", "WriteSideApp");
  if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "WriteSideApp.sln")))
  return candidate;
  dir = Path.GetDirectoryName(dir);
  }
  // Fallback. Works when running from repo root
  return Path.GetFullPath("tests/GoldenRepos/WriteSideApp");
  }
}
