using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

internal sealed class TestBlastRadiusBridge : IBlastRadiusProvider
{
    public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
        => Lifeblood.Analysis.BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
}

/// <summary>
/// Stage 2.C — pins the blast-radius grouping contract. Field-report
/// 2026-05-11 P1: <c>affectedCount=221</c> alone is a warning, not a
/// triage tool. Grouping by path bucket (Production/Test/Editor/Generated)
/// and by module/asmdef converts a flat count into actionable signal.
/// </summary>
public class BlastRadiusGroupingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();
    private readonly AnalysisConfig _config = new() { RetainCompilations = true };
    private readonly LifebloodMcpProvider _provider = new(new TestBlastRadiusBridge());

    public BlastRadiusGroupingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-blastgroup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ClassifyBlastRadius_GroupsByPathBucket_TestAndProductionSeparated()
    {
        // Production file + a Tests/ file each reference the same target type.
        // Blast radius from the target should split exactly across buckets.
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Tests"));
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "src", "Target.cs"),
            "namespace App; public class Target { public void Do() {} }");
        File.WriteAllText(Path.Combine(_tempDir, "src", "Consumer.cs"),
            "namespace App; public class Consumer { public void Use() { var t = new Target(); t.Do(); } }");
        File.WriteAllText(Path.Combine(_tempDir, "Tests", "TargetTests.cs"),
            "namespace App; public class TargetTests { public void RunOne() { var t = new Target(); t.Do(); } }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var groups = _provider.ClassifyBlastRadius(graph, "type:App.Target", maxDepth: 10, maxResults: 10);

        Assert.True(groups.TotalAffected >= 2,
            $"Expected at least Consumer + TargetTests to land in affected (got {groups.TotalAffected})");
        Assert.True(groups.ByBucket.ContainsKey("Production"),
            "Consumer.cs must classify into Production bucket");
        Assert.True(groups.ByBucket.ContainsKey("Test"),
            "Tests/TargetTests.cs must classify into Test bucket");
        Assert.True(groups.ByBucket["Production"].Count > 0);
        Assert.True(groups.ByBucket["Test"].Count > 0);
    }

    [Fact]
    public void ClassifyBlastRadius_GroupsByModule_CrossModuleSplit()
    {
        // Two-module project where ModuleB references ModuleA.Target.
        // Blast radius from Target should attribute the Consumer in ModuleB
        // under the ModuleB bucket (NOT ModuleA).
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));
        var csprojA = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>ModuleA</AssemblyName></PropertyGroup></Project>";
        var csprojB = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>ModuleB</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include=""..\ModuleA\ModuleA.csproj"" /></ItemGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), csprojA);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), csprojB);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "Target.cs"),
            "namespace A; public class Target { public void Do() {} }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "Consumer.cs"),
            "namespace B; public class Consumer { public void Use() { var t = new A.Target(); t.Do(); } }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var groups = _provider.ClassifyBlastRadius(graph, "type:A.Target", maxDepth: 10, maxResults: 10);

        Assert.True(groups.ByModule.ContainsKey("ModuleB"),
            $"Cross-module consumer must land under ModuleB. Got modules: " +
            string.Join(", ", groups.ByModule.Keys));
        Assert.True(groups.ByModule["ModuleB"].Count > 0);
    }

    [Fact]
    public void ClassifyBlastRadius_NoMatches_EmptyGroupsWithZeroCount()
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Unused.cs"),
            "namespace App; public class Unused {}");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var groups = _provider.ClassifyBlastRadius(graph, "type:App.Unused", maxDepth: 10, maxResults: 10);

        Assert.Equal(0, groups.TotalAffected);
        Assert.Equal(0, groups.DirectDependants);
        Assert.Empty(groups.ByBucket);
        Assert.Empty(groups.ByModule);
    }

    [Fact]
    public void ClassifyBlastRadius_PreviewSizeCappedByMaxResults()
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), csproj);
        // 5 consumers in the same Production bucket.
        File.WriteAllText(Path.Combine(_tempDir, "Target.cs"),
            "namespace App; public class Target { public void Do() {} }");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"Consumer{i}.cs"),
                $"namespace App; public class Consumer{i} {{ public void Use() {{ var t = new Target(); t.Do(); }} }}");
        }

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var groups = _provider.ClassifyBlastRadius(graph, "type:App.Target", maxDepth: 10, maxResults: 2);

        Assert.True(groups.ByBucket.ContainsKey("Production"));
        var prod = groups.ByBucket["Production"];
        Assert.True(prod.Count >= 5, $"Expected ≥5 Production hits, got {prod.Count}");
        Assert.True(prod.Preview.Length <= 2, $"Preview must respect maxResults=2, got {prod.Preview.Length}");
    }

    [Fact]
    public void ClassifyBlastRadius_PreviewSizeZero_SkipsPreviewEntirely()
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>App</AssemblyName></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Target.cs"),
            "namespace App; public class Target { public void Do() {} }");
        File.WriteAllText(Path.Combine(_tempDir, "Consumer.cs"),
            "namespace App; public class Consumer { public void Use() { var t = new Target(); t.Do(); } }");

        var analyzer = new RoslynWorkspaceAnalyzer(_fs);
        var graph = analyzer.AnalyzeWorkspace(_tempDir, _config);

        var groups = _provider.ClassifyBlastRadius(graph, "type:App.Target", maxDepth: 10, maxResults: 0);

        Assert.True(groups.ByBucket.ContainsKey("Production"));
        Assert.Empty(groups.ByBucket["Production"].Preview);
    }
}
