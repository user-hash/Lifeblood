using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ANALYZE-FALLBACK-001 wire-shape contract. Pins the JSON response
/// shape of <c>lifeblood_analyze</c> as observed by an MCP client through
/// <see cref="GraphSession.Load"/>. The contract this file enforces:
///
/// <list type="bullet">
///   <item><c>mode</c> reports what the adapter DID
///         (<c>full</c> / <c>incremental</c> / <c>incremental-noop</c> / <c>rejected</c>).</item>
///   <item><c>requestedMode</c> reports what the caller ASKED
///         (<c>full</c> / <c>incremental</c>). Disambiguates fallback from
///         original-intent-full without inventing a hybrid mode value.</item>
///   <item><c>fallbackReason</c> + <c>fallbackDetail</c> appear whenever the
///         cheap path could not be honored cleanly — both on <c>rejected</c>
///         and on widened <c>full</c> + <c>requestedMode:incremental</c>.</item>
///   <item>Rejection responses additionally carry <c>canRetryFull:true</c>
///         and a <c>suggestedRetry</c> object so the agent's next move is
///         self-documenting.</item>
///   <item>Rejection is a NORMAL structured result — not a transport / tool
///         error. The wire shape stays inside the same JSON envelope.</item>
/// </list>
/// </summary>
public class AnalyzeWireShapeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PhysicalFileSystem _fs = new();

    public AnalyzeWireShapeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lifeblood-wire-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // INV-ANALYZE-FALLBACK-001 — the most important wire-shape contract test.
    // Pre-fix, calling Load(incremental:true) on a fresh session silently
    // fell through to full because the GraphSession-level CanIncremental
    // gate routed it past LoadIncremental, and the typed
    // FallbackReason.NoPriorAnalysis from the adapter was never reached
    // through the MCP layer. Reviewer's B3 dogfood case.
    [Fact]
    public void Load_IncrementalOnFreshSession_AllowFallbackFalse_RejectsWithNoPriorAnalysis()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);

        // No prior analyze. incremental:true with default allowFullFallback:false
        // must reject — otherwise the agent's "be cheap" intent is silently
        // overridden by a full re-analyze.
        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("noPriorAnalysis", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("fallbackDetail").GetString()));
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());

        var sug = doc.RootElement.GetProperty("suggestedRetry");
        Assert.True(sug.GetProperty("incremental").GetBoolean());
        Assert.True(sug.GetProperty("allowFullFallback").GetBoolean());

        // Rejection means no work done — summary is null.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Load_IncrementalOnFreshSession_AllowFallbackTrue_FallsThroughToFull()
    {
        // Same trigger, opposite caller policy. With allowFullFallback:true the
        // first-call incremental:true should silently widen to full. Wire shape
        // surfaces what the adapter DID (full) + what the caller ASKED
        // (incremental) without losing the cache-miss signal.
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("noPriorAnalysis", doc.RootElement.GetProperty("fallbackReason").GetString());
        var receipt = doc.RootElement.GetProperty("acceptedChanges");
        Assert.Equal("fullFallback", receipt.GetProperty("scanMode").GetString());
        Assert.True(receipt.GetProperty("fullFallback").GetBoolean());
        Assert.Equal(1, receipt.GetProperty("counts").GetProperty("reanalyzedSourceFiles").GetInt32());
        Assert.Equal(0, receipt.GetProperty("counts").GetProperty("mtimeTouchedSourceFiles").GetInt32());
        Assert.Equal(0, receipt.GetProperty("counts").GetProperty("contentChangedSourceFiles").GetInt32());
        var interpretation = receipt.GetProperty("interpretation");
        Assert.Equal("fullFallbackReanalysis", interpretation.GetProperty("work").GetString());
        Assert.Equal("reanalyzedOrDeleted", interpretation.GetProperty("changedSourceFilesMeaning").GetString());
        Assert.Equal("none", interpretation.GetProperty("contentChangeStatus").GetString());
        Assert.Contains("not evidence of source content churn", interpretation.GetProperty("summary").GetString());
        Assert.Empty(receipt.GetProperty("files").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Load_FullAnalyze_WireCarriesModeFullAndRequestedModeFull()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("full", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        AssertNullProperty(doc.RootElement, "fallbackDetail");
        AssertNullProperty(doc.RootElement, "canRetryFull");
        AssertNullProperty(doc.RootElement, "suggestedRetry");
        AssertNullProperty(doc.RootElement, "acceptedChanges");
    }

    [Fact]
    public void Load_ReadOnlyThenWriteSideIncremental_RejectsWithCompilationStateRecovery()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false, readOnly: true);

        Assert.True(session.IsLoaded);
        Assert.False(session.HasCompilationState);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true,
                                readOnly: false,
                                allowFullFallback: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("compilationStateUnavailable", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.Contains("readOnly:true", doc.RootElement.GetProperty("fallbackDetail").GetString());
        Assert.Contains("incremental:false", doc.RootElement.GetProperty("fallbackDetail").GetString());
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());
        Assert.False(session.HasCompilationState);
    }

    [Fact]
    public void Load_ReadOnlyThenWriteSideIncremental_AllowFullFallbackRestoresCompilationState()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false, readOnly: true);

        Assert.False(session.HasCompilationState);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true,
                                readOnly: false,
                                allowFullFallback: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("compilationStateUnavailable", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
        Assert.True(session.HasCompilationState);
    }

    [Fact]
    public void Load_FullAnalyze_ExcludePaths_DropsMatchingSourceFromGraph()
    {
        WriteProjectWithVendoredExample();
        var session = new GraphSession(_fs);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: false,
                                excludePaths: new[] { "*/Examples*/*" });
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.NotNull(session.Graph);
        Assert.Contains(session.Graph!.Symbols, s => s.Id == "type:Test.Keep");
        Assert.DoesNotContain(session.Graph.Symbols, s => s.Id == "type:Test.VendoredDemo");
    }

    [Fact]
    public void Load_IncrementalNoEdits_WireCarriesIncrementalNoopAndRequestedIncremental()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        // Second call with incremental:true on an unchanged workspace must
        // hit the "incremental-noop" fast path. Default allowFullFallback
        // (false) is irrelevant here because no fallback is triggered.
        var json = session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("incremental-noop", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        AssertNullProperty(doc.RootElement, "canRetryFull");
        var receipt = doc.RootElement.GetProperty("acceptedChanges");
        Assert.Equal("summary", receipt.GetProperty("mode").GetString());
        Assert.Equal("filesystemPrefilter", receipt.GetProperty("scanMode").GetString());
        Assert.Equal(0, receipt.GetProperty("evidenceFileCount").GetInt32());
        Assert.Empty(receipt.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void Load_IncrementalWithChangedExcludePaths_RejectsWithAnalysisScopeChanged()
    {
        WriteSingleFileProject("public class Foo { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true,
                                allowFullFallback: false,
                                excludePaths: new[] { "src/*" });
        var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("analysisScopeChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("fallbackDetail").GetString()));
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Load_IncrementalWithChangedDefineProfileOrder_RejectsWithAnalysisScopeChanged()
    {
        WriteSingleFileProject("public class Foo { }");
        Directory.CreateDirectory(Path.Combine(_tempDir, "Library"));
        using var session = new GraphSession(_fs);
        session.Load(
            _tempDir,
            graphPath: null,
            rulesPath: null,
            defineProfiles: new[] { "Editor", "Player" });
        var committedSnapshot = session.CurrentSnapshot;

        var json = session.Load(
            _tempDir,
            graphPath: null,
            rulesPath: null,
            incremental: true,
            allowFullFallback: false,
            defineProfiles: new[] { "Player", "Editor" });
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("analysisScopeChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());
        Assert.Same(committedSnapshot, session.CurrentSnapshot);
    }

    [Fact]
    public void Load_MultiProfileUnityAnalyze_ProjectsProfileApplicability()
    {
        WriteUnityProfileApplicabilityWorkspace();
        using var session = new GraphSession(_fs);

        var json = session.Load(
            _tempDir,
            graphPath: null,
            rulesPath: null,
            defineProfiles: new[] { "Player", "Editor" });
        using var doc = JsonDocument.Parse(json);

        var applicability = doc.RootElement.GetProperty("profileApplicability");
        Assert.True(applicability.GetProperty("isUnityWorkspace").GetBoolean());
        Assert.Equal(new[] { "Player", "Editor" }, JsonStrings(applicability.GetProperty("profiles")));
        Assert.Equal(3, applicability.GetProperty("moduleCount").GetInt32());
        Assert.Equal(2, applicability.GetProperty("includedModuleCountsByProfile").GetProperty("Player").GetInt32());
        Assert.Equal(1, applicability.GetProperty("excludedModuleCountsByProfile").GetProperty("Player").GetInt32());

        var modules = applicability.GetProperty("modules").EnumerateArray().ToArray();
        var editor = modules.Single(module => module.GetProperty("name").GetString() == "Editor");
        Assert.Equal("Editor:5", editor.GetProperty("unityProjectType").GetString());
        Assert.True(editor.GetProperty("isEditorOnly").GetBoolean());
        Assert.Equal(new[] { "Editor" }, JsonStrings(editor.GetProperty("includedProfiles")));
        Assert.Equal(new[] { "Player" }, JsonStrings(editor.GetProperty("excludedProfiles")));

        var exclusion = Assert.Single(editor.GetProperty("exclusions").EnumerateArray());
        Assert.Equal("Player", exclusion.GetProperty("profile").GetString());
        Assert.Equal(
            ProfileApplicabilityReason.EditorOnlyModuleExcludedByProfile,
            exclusion.GetProperty("reason").GetString());
    }

    [Fact]
    public void Load_IncrementalAfterEdit_WireCarriesIncrementalMode()
    {
        var filePath = WriteSingleFileProject("public class Foo { }");
        var secondPath = Path.Combine(_tempDir, "Second.cs");
        File.WriteAllText(secondPath, "public class Second { }");
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        Thread.Sleep(50);
        File.WriteAllText(filePath, "public class Foo { public void Bar() { } }");
        File.WriteAllText(secondPath, "public class Second { public void Baz() { } }");

        var json = session.Load(
            _tempDir,
            graphPath: null,
            rulesPath: null,
            incremental: true,
            acceptedChangeReceipt: new AcceptedChangeReceiptRequest(
                AcceptedChangeReceiptMode.Detail,
                limit: 1));
        var doc = JsonDocument.Parse(json);

        Assert.Equal("incremental", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(doc.RootElement, "fallbackReason");
        var receipt = doc.RootElement.GetProperty("acceptedChanges");
        var counts = receipt.GetProperty("counts");
        Assert.Equal("detail", receipt.GetProperty("mode").GetString());
        Assert.Equal("filesystemPrefilter", receipt.GetProperty("scanMode").GetString());
        Assert.Equal(2, counts.GetProperty("changedSourceFiles").GetInt32());
        Assert.Equal(2, counts.GetProperty("contentChangedSourceFiles").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("changedSourceFiles").GetInt32());
        Assert.Equal(2, receipt.GetProperty("evidenceFileCount").GetInt32());
        Assert.Equal(1, receipt.GetProperty("returnedFileCount").GetInt32());
        Assert.Equal(1, receipt.GetProperty("omittedFileCount").GetInt32());
        Assert.True(receipt.GetProperty("truncated").GetBoolean());
        var file = Assert.Single(receipt.GetProperty("files").EnumerateArray());
        Assert.Equal("Program.cs", file.GetProperty("path").GetString());
        Assert.True(file.GetProperty("reanalyzed").GetBoolean());
        Assert.True(file.GetProperty("contentChanged").GetBoolean());
    }

    [Fact]
    public void Load_IncrementalAfterModuleSetChanged_RejectedShape_AllowFullFallbackFalse()
    {
        // INV-ANALYZE-FALLBACK-001 rejection wire shape — the contract this
        // whole change exists to enforce. Two-module workspace, full analyze,
        // delete one module's csproj, retry incremental:true with default
        // (allowFullFallback:false). Wire shape MUST carry canRetryFull +
        // suggestedRetry so the agent can self-correct.
        WriteTwoModuleProject();
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("rejected", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("fallbackDetail").GetString()));
        Assert.True(doc.RootElement.GetProperty("canRetryFull").GetBoolean());

        var suggested = doc.RootElement.GetProperty("suggestedRetry");
        Assert.True(suggested.GetProperty("incremental").GetBoolean());
        Assert.True(suggested.GetProperty("allowFullFallback").GetBoolean());

        // Rejection means no work done — summary block is null.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void Load_IncrementalAfterModuleSetChanged_FullFallbackShape_AllowFullFallbackTrue()
    {
        // Same trigger, opposite policy. mode == "full" (the truth: the adapter
        // did a full re-analyze) + requestedMode == "incremental" (the truth:
        // the caller asked for cheap) + fallbackReason populated so the cache
        // miss stays visible. canRetryFull is null because the work succeeded.
        WriteTwoModuleProject();
        var session = new GraphSession(_fs);
        session.Load(_tempDir, graphPath: null, rulesPath: null, incremental: false);

        File.Delete(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"));
        File.Delete(Path.Combine(_tempDir, "ModuleB", "B.cs"));

        var json = session.Load(_tempDir, graphPath: null, rulesPath: null,
                                incremental: true, allowFullFallback: true);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", doc.RootElement.GetProperty("fallbackReason").GetString());
        AssertNullProperty(doc.RootElement, "canRetryFull");
        AssertNullProperty(doc.RootElement, "suggestedRetry");

        // Work succeeded — summary populated.
        Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("summary").ValueKind);
    }

    private static void AssertNullProperty(JsonElement element, string propertyName)
    {
        Assert.True(element.TryGetProperty(propertyName, out var prop),
            $"Expected property '{propertyName}' to exist on the response.");
        Assert.Equal(JsonValueKind.Null, prop.ValueKind);
    }

    // ── Helpers ──

    private static string[] JsonStrings(JsonElement element)
        => element.EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

    private string WriteSingleFileProject(string code)
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        var csPath = Path.Combine(_tempDir, "Program.cs");
        File.WriteAllText(csPath, code);
        return csPath;
    }

    private void WriteTwoModuleProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleA"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "ModuleB"));

        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "ModuleA.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "ModuleB.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "ModuleA", "A.cs"), "namespace A; public class ATypeA { }");
        File.WriteAllText(Path.Combine(_tempDir, "ModuleB", "B.cs"), "namespace B; public class BTypeB { }");
    }

    private void WriteProjectWithVendoredExample()
    {
        var csproj = @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_tempDir, "TestProject.csproj"), csproj);
        File.WriteAllText(Path.Combine(_tempDir, "Keep.cs"), "namespace Test; public class Keep { }");

        var examplesDir = Path.Combine(_tempDir, "Assets", "TextMesh Pro", "Examples & Extras");
        Directory.CreateDirectory(examplesDir);
        File.WriteAllText(Path.Combine(examplesDir, "Demo.cs"), "namespace Test; public class VendoredDemo { }");
    }

    private void WriteUnityProfileApplicabilityWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Library"));
        WriteUnityProject("Runtime", "Game:1", "Runtime.cs");
        WriteUnityProject("Editor", "Editor:5", "Editor.cs");
        WriteUnityProject("EditorPlugin", "EditorPlugins:7", "EditorPlugin.cs");
        File.WriteAllText(Path.Combine(_tempDir, "Runtime.cs"), "namespace App; public sealed class RuntimeType { }");
        File.WriteAllText(Path.Combine(_tempDir, "Editor.cs"), "namespace App; public sealed class EditorType { }");
        File.WriteAllText(Path.Combine(_tempDir, "EditorPlugin.cs"), "namespace App; public sealed class EditorPluginType { }");
    }

    private void WriteUnityProject(
        string assemblyName,
        string unityProjectType,
        string sourceFile)
    {
        File.WriteAllText(Path.Combine(_tempDir, $"{assemblyName}.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>{{assemblyName}}</AssemblyName>
                <DefineConstants>UNITY_EDITOR</DefineConstants>
                <UnityProjectType>{{unityProjectType}}</UnityProjectType>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{{sourceFile}}" />
              </ItemGroup>
            </Project>
            """);
    }
}
