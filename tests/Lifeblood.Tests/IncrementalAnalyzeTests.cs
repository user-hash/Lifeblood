using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Tests for incremental re-analyze. Uses real temp files to verify
/// timestamp-based change detection and selective recompilation.
/// </summary>
public class IncrementalAnalyzeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();
    private readonly AnalysisConfig _config = new() { RetainCompilations = true };

    public IncrementalAnalyzeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void IncrementalAnalyze_NoChanges_ReturnsZeroChanged()
    {
        WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.True(analyzer.HasSnapshot);

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.Null(r.Reason);
        Assert.Equal(0, r.ChangedFileCount);
        Assert.NotNull(r.Graph);
        Assert.True(r.Graph!.Symbols.Count > 0);
    }

    [Fact]
    public void IncrementalAnalyze_FileModified_DetectsChange()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Verify initial state
        Assert.Contains(graph1.Symbols, s => s.Name == "Foo" && s.Kind == SymbolKind.Type);

        // Modify the file — add a method
        Thread.Sleep(50); // ensure timestamp changes
        File.WriteAllText(filePath, "public class Foo { public void Bar() { } }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.True(r.ChangedFileCount > 0, "Should detect changed file");
        Assert.NotNull(r.Graph);
        Assert.Contains(r.Graph!.Symbols, s => s.Name == "Bar" && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public void IncrementalAnalyze_AddType_Detected()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.DoesNotContain(graph1.Symbols, s => s.Name == "Extra");

        // Add a new type to the file
        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { }\npublic class Extra { }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        Assert.Contains(r.Graph!.Symbols, s => s.Name == "Extra" && s.Kind == SymbolKind.Type);
    }

    [Fact]
    public void IncrementalAnalyze_RemoveMethod_UpdatesGraph()
    {
        var filePath = WriteSingleFileProject("public class Foo { public void Remove() { } }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.Contains(graph1.Symbols, s => s.Name == "Remove");

        // Remove the method
        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        Assert.DoesNotContain(r.Graph!.Symbols, s => s.Name == "Remove" && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public void IncrementalAnalyze_UnchangedFileSymbolsPreserved()
    {
        // Two-file project: only change one
        WriteTwoFileProject(
            "public class Stable { public void Keep() { } }",
            "public class Dynamic { }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        Assert.Contains(graph1.Symbols, s => s.Name == "Keep");
        Assert.Contains(graph1.Symbols, s => s.Name == "Dynamic");

        // Modify only Dynamic.cs
        var dynamicPath = Path.Combine(_tempDir, "Dynamic.cs");
        Thread.Sleep(50);
        File.WriteAllText(dynamicPath, "public class Dynamic { public int Value { get; set; } }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.Equal(1, r.ChangedFileCount); // only Dynamic.cs changed
        Assert.NotNull(r.Graph);
        Assert.Contains(r.Graph!.Symbols, s => s.Name == "Keep"); // Stable.cs preserved
        Assert.Contains(r.Graph.Symbols, s => s.Name == "Value" && s.Kind == SymbolKind.Property); // new member
    }

    [Fact]
    public void IncrementalAnalyze_FileEdgesUpdated()
    {
        // Initial: Foo references Bar
        WriteTwoFileProject(
            "public class Foo { private Bar _b; }",
            "public class Bar { }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Should have file-level edge Stable.cs → Dynamic.cs
        var fileEdge1 = graph1.Edges.FirstOrDefault(e =>
            e.SourceId.Contains("Stable") && e.TargetId.Contains("Dynamic") && e.Kind == EdgeKind.References);
        Assert.NotNull(fileEdge1);

        // Modify Foo to NOT reference Bar anymore
        var stablePath = Path.Combine(_tempDir, "Stable.cs");
        Thread.Sleep(50);
        File.WriteAllText(stablePath, "public class Foo { }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        // File-level edge should be gone
        var fileEdge2 = r.Graph!.Edges.FirstOrDefault(e =>
            e.SourceId.Contains("Stable") && e.TargetId.Contains("Dynamic") && e.Kind == EdgeKind.References);
        Assert.Null(fileEdge2);
    }

    // INV-ANALYZE-FALLBACK-001: NoPriorAnalysis is always Rejected. Pre-fix
    // this path threw InvalidOperationException; the typed result is the
    // fail-loud-without-throwing replacement.
    [Fact]
    public void IncrementalAnalyze_WithoutPriorAnalysis_ReturnsRejectedNoPriorAnalysis()
    {
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);

        Assert.False(analyzer.HasSnapshot);

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Rejected, r.Mode);
        Assert.Equal(FallbackReason.NoPriorAnalysis, r.Reason);
        Assert.Null(r.Graph);
        Assert.Equal(0, r.ChangedFileCount);
        Assert.NotNull(r.Detail);
    }

    [Fact]
    public void IncrementalAnalyze_WithoutPriorAnalysis_AllowFullFallbackTrue_StillRejects()
    {
        // NoPriorAnalysis cannot fall back even with AllowFullFallback=true
        // — there's no projectRoot in scope without a snapshot. The remediation
        // is fixed: caller must invoke AnalyzeWorkspace explicitly.
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var allowingConfig = new AnalysisConfig
        {
            RetainCompilations = true,
            AllowFullFallback = true,
        };

        var r = analyzer.IncrementalAnalyze(allowingConfig);

        Assert.Equal(IncrementalMode.Rejected, r.Mode);
        Assert.Equal(FallbackReason.NoPriorAnalysis, r.Reason);
    }

    // ── Csproj-edit invalidation (INV-BCL-005) ──
    // See .claude/plans/bcl-ownership-fix.md §8 and §9.3.

    [Fact]
    public void IncrementalAnalyze_CsprojEdited_TriggersRediscoveryAndRecompile()
    {
        // Initial state: plain SDK csproj with no Reference elements.
        // Discovery reports BclOwnership = HostProvided.
        WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Edit the csproj to add a Reference Include="netstandard" element.
        // Discovery should now report ModuleProvided. The .cs file is unchanged
        // — only the csproj timestamp moves. Without csproj-timestamp tracking,
        // incremental would silently skip this module and the BclOwnership flag
        // would stay HostProvided forever (silent reintroduction of double-BCL bug).
        Thread.Sleep(50); // ensure timestamp changes
        var csprojPath = Path.Combine(_tempDir, "TestProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""netstandard"" />
  </ItemGroup>
</Project>");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        // Csproj-only edit must mark the module's .cs files as changed and
        // route them through the recompilation pipeline. Csproj edits are
        // handled INSIDE the per-file incremental walk, NOT as a fallback —
        // the result is Mode=Incremental with a positive ChangedFileCount.
        Assert.True(r.ChangedFileCount > 0,
            "Csproj edit must trigger recompilation (ChangedFileCount > 0). " +
            "If this is 0, csproj-timestamp invalidation is broken and BclOwnership " +
            "edits go undetected — silent reintroduction of the double-BCL bug.");

        // The fresh ModuleInfo from rediscovery must have BclOwnership = ModuleProvided.
        // Verify by re-running discovery directly (the analyzer's internal state
        // also has it but we don't expose snapshot publicly).
        var modules = new RoslynModuleDiscovery(_fs).DiscoverModules(_tempDir);
        Assert.Single(modules);
        Assert.Equal(BclOwnershipMode.ModuleProvided, modules[0].BclOwnership);
    }

    [Fact]
    public void IncrementalAnalyze_CsprojUntouched_DoesNotForceRecompile()
    {
        // Negative test: a .cs file edit alone must not also trigger the
        // csproj-change path. The .cs path catches it for source-extraction
        // reasons, but csprojChangedModules must be empty.
        var filePath = WriteSingleFileProject("public class Foo { }");
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Touch only the .cs file. Csproj timestamp is unchanged.
        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { public void Bar() { } }");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        // The .cs change is detected, so ChangedFileCount > 0 — but for
        // .cs reasons, not csproj reasons. The new symbol must be in the graph.
        Assert.True(r.ChangedFileCount > 0);
        Assert.Contains(r.Graph!.Symbols, s => s.Name == "Bar" && s.Kind == SymbolKind.Method);
    }

    // ── Fallback gate (INV-ANALYZE-FALLBACK-001) ──
    // Module-set drift triggers the gate. Two policies, two outcomes.

    [Fact]
    public void IncrementalAnalyze_ModuleSetChanged_AllowFullFallbackFalse_RejectsWithReason()
    {
        // Two-csproj workspace. Analyze. Delete one csproj → module set drifts.
        // Default config (AllowFullFallback=false): adapter must reject and
        // surface the reason. No work done, Graph is null.
        WriteTwoModuleProject();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        // Drop one module by deleting its csproj
        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Rejected, r.Mode);
        Assert.Equal(FallbackReason.ModuleSetChanged, r.Reason);
        Assert.Null(r.Graph);
        Assert.Equal(0, r.ChangedFileCount);
        Assert.NotNull(r.Detail);
    }

    [Fact]
    public void IncrementalAnalyze_ModuleSetChanged_AllowFullFallbackTrue_WidensToFullWithReason()
    {
        // Same trigger, different policy. AllowFullFallback=true: adapter
        // widens to a full re-analyze and reports both the result AND the
        // reason it widened. Caller sees the cache miss without losing
        // the result.
        WriteTwoModuleProject();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        analyzer.AnalyzeWorkspace(_tempDir, _config);

        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var allowingConfig = new AnalysisConfig
        {
            RetainCompilations = true,
            AllowFullFallback = true,
        };
        var r = analyzer.IncrementalAnalyze(allowingConfig);

        Assert.Equal(IncrementalMode.FullFallback, r.Mode);
        Assert.Equal(FallbackReason.ModuleSetChanged, r.Reason);
        Assert.NotNull(r.Graph);
        Assert.NotNull(r.Detail);
        // After widening, only ModuleA's symbols remain (B was deleted).
        Assert.Contains(r.Graph!.Symbols, s => s.Name == "ATypeA");
        Assert.DoesNotContain(r.Graph.Symbols, s => s.Name == "BTypeB");
    }

    // ── Cross-module edge integrity (INV-INCREMENTAL-XREF-001 / closes LB-BUG-020) ──
    //
    // The 2026-05-10 dogfood pass against a real-world Unity workspace found
    // incremental analyze silently dropping cross-module edges in proportion
    // to the unchanged-module fan-in on a single-file touch. Root cause:
    // ModuleCompilationBuilder.
    // ProcessInOrder kept a LOCAL `downgraded` Dictionary<string, MetadataReference>
    // that was discarded at end-of-call. Full analyze populated it for every
    // module; incremental analyze called ProcessInOrder with only the changed
    // modules subset, so unchanged dependencies had no metadata reference,
    // every cross-module symbol bound to an error symbol, and the corresponding
    // edges were dropped at GraphBuilder's dangling-edge filter.
    //
    // The fix persists the downgraded refs across calls via
    // AnalysisSnapshot.DowngradedRefs, threaded through ProcessInOrder as
    // a carry-in/carry-out parameter. Touching a file in module B must leave
    // every B→A edge intact: same kind, same target, same count.

    [Fact]
    public void IncrementalAnalyze_CrossModuleEdges_IdenticalAfterContentlessTouch()
    {
        // Two-module project, B depends on A and references A.TypeA.
        WriteCrossModuleProject();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        int totalEdges1 = graph1.Edges.Count;
        int xrefEdges1 = CountCrossModuleEdges(graph1);
        Assert.True(xrefEdges1 > 0,
            "Setup precondition: cross-module project must produce >0 B->A edges " +
            "in full analyze. If 0, the test scaffold itself is broken.");

        // Touch B.cs with identical content. Roslyn re-binds. Pre-fix, this
        // alone dropped cross-module edges because B's recompiled Compilation
        // had no metadata ref to A.
        var bFile = Path.Combine(_tempDir, "ModuleB", "B.cs");
        var bContent = File.ReadAllText(bFile);
        Thread.Sleep(50);
        File.WriteAllText(bFile, bContent);

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);

        int totalEdges2 = r.Graph!.Edges.Count;
        int xrefEdges2 = CountCrossModuleEdges(r.Graph);

        Assert.Equal(xrefEdges1, xrefEdges2);
        Assert.Equal(totalEdges1, totalEdges2);
    }

    [Fact]
    public void IncrementalAnalyze_CrossModuleEdges_PreservedAfterContentChangeInDependent()
    {
        // Add an unrelated method to B. The B→A reference edges must survive.
        WriteCrossModuleProject();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        int xrefEdges1 = CountCrossModuleEdges(graph1);
        Assert.True(xrefEdges1 > 0);

        var bFile = Path.Combine(_tempDir, "ModuleB", "B.cs");
        Thread.Sleep(50);
        File.WriteAllText(bFile,
            "namespace B; public class TypeB { " +
            "  public void Consume() { var a = new A.TypeA(); a.Method(); } " +
            "  public int Added() => 42; " +
            "}");

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        int xrefEdges2 = CountCrossModuleEdges(r.Graph!);
        Assert.Equal(xrefEdges1, xrefEdges2);
    }

    [Fact]
    public void IncrementalAnalyze_TransitiveDependencyChain_AllCrossModuleEdgesPreserved()
    {
        // Three-module chain: C → B → A. Touch C only.
        // C's compilation must see both B AND A as metadata refs (Roslyn
        // compilation refs are NOT transitive — every assembly whose types
        // appear in C's source must be an explicit reference). The transitive
        // walk in ComputeTransitiveDependencies + the persistent carry must
        // jointly cover this. Pre-fix, neither A nor B was in `downgraded`
        // during C's incremental recompile, so all of C's cross-module edges
        // dropped.
        WriteThreeModuleChain();
        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph1 = analyzer.AnalyzeWorkspace(_tempDir, _config);

        int totalXref1 = CountModuleToModuleEdges(graph1);
        Assert.True(totalXref1 > 0,
            "Setup: 3-module chain must produce >0 cross-module edges");

        // Touch C.cs
        var cFile = Path.Combine(_tempDir, "ModuleC", "C.cs");
        var cContent = File.ReadAllText(cFile);
        Thread.Sleep(50);
        File.WriteAllText(cFile, cContent);

        var r = analyzer.IncrementalAnalyze(_config);

        Assert.Equal(IncrementalMode.Incremental, r.Mode);
        Assert.NotNull(r.Graph);
        int totalXref2 = CountModuleToModuleEdges(r.Graph!);
        Assert.Equal(totalXref1, totalXref2);
    }

    private void WriteThreeModuleChain()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleC"));

        var sdk = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>{0}</AssemblyName>
  </PropertyGroup>
  {1}
</Project>";

        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"),
            string.Format(sdk, "ModuleA", ""));
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"),
            string.Format(sdk, "ModuleB",
                @"<ItemGroup><ProjectReference Include=""..\ModuleA\ModuleA.csproj"" /></ItemGroup>"));
        File.WriteAllText(Path.Combine(_tempDir, "ModuleC", "ModuleC.csproj"),
            string.Format(sdk, "ModuleC",
                @"<ItemGroup>
                    <ProjectReference Include=""..\ModuleA\ModuleA.csproj"" />
                    <ProjectReference Include=""..\ModuleB\ModuleB.csproj"" />
                  </ItemGroup>"));

        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "A.cs"),
            "namespace A; public class TypeA { public void MA() { } }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "B.cs"),
            "namespace B; public class TypeB { public void MB() { var a = new A.TypeA(); a.MA(); } }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleC", "C.cs"),
            "namespace C; public class TypeC { public void MC() { var b = new B.TypeB(); b.MB(); var a = new A.TypeA(); a.MA(); } }");
    }

    private static int CountModuleToModuleEdges(SemanticGraph g)
    {
        var symById = g.Symbols.ToDictionary(s => s.Id, s => s);
        string? ModuleOf(string symId)
        {
            string? cursor = symId;
            int hops = 0;
            while (cursor != null && hops++ < 16)
            {
                if (!symById.TryGetValue(cursor, out var sym)) return null;
                if (sym.Kind == SymbolKind.Module) return sym.Name;
                cursor = sym.ParentId;
            }
            return null;
        }
        int count = 0;
        foreach (var e in g.Edges)
        {
            if (e.Kind == EdgeKind.Contains) continue;
            if (e.Kind == EdgeKind.DependsOn) continue; // module→module edges, not what we test
            var src = ModuleOf(e.SourceId);
            var tgt = ModuleOf(e.TargetId);
            if (src != null && tgt != null && src != tgt) count++;
        }
        return count;
    }

    private static int CountCrossModuleEdges(SemanticGraph g)
    {
        // Cross-module edges are non-Contains edges whose source qualified-name
        // names module B and target qualified-name names module A. Walk symbols
        // to classify by containing module via Parent chain.
        var symById = g.Symbols.ToDictionary(s => s.Id, s => s);
        string? ModuleOf(string symId)
        {
            string? cursor = symId;
            int hops = 0;
            while (cursor != null && hops++ < 16)
            {
                if (!symById.TryGetValue(cursor, out var sym)) return null;
                if (sym.Kind == SymbolKind.Module) return sym.Name;
                cursor = sym.ParentId;
            }
            return null;
        }
        int count = 0;
        foreach (var e in g.Edges)
        {
            if (e.Kind == EdgeKind.Contains) continue;
            var src = ModuleOf(e.SourceId);
            var tgt = ModuleOf(e.TargetId);
            if (src == "ModuleB" && tgt == "ModuleA") count++;
        }
        return count;
    }

    private void WriteCrossModuleProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));

        var csprojA = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>ModuleA</AssemblyName>
  </PropertyGroup>
</Project>";

        var csprojB = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>ModuleB</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\ModuleA\ModuleA.csproj"" />
  </ItemGroup>
</Project>";

        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), csprojA);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), csprojB);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "A.cs"),
            "namespace A; public class TypeA { public void Method() { } }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "B.cs"),
            "namespace B; public class TypeB { public void Consume() { var a = new A.TypeA(); a.Method(); } }");
    }

    // ── Helpers ──

    private void WriteTwoModuleProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));

        var csprojA = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        var csprojB = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";

        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), csprojA);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), csprojB);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "A.cs"), "namespace A; public class ATypeA { }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "B.cs"), "namespace B; public class BTypeB { }");
    }

    private string WriteSingleFileProject(string code)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);

        var csPath = Path.Combine(_tempDir, "Program.cs");
        File.WriteAllText(csPath, code);
        return csPath;
    }

    private void WriteTwoFileProject(string code1, string code2)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Stable.cs"), code1);
        File.WriteAllText(Path.Combine(_tempDir, "Dynamic.cs"), code2);
    }
}
