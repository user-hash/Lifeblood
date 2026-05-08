using System.Linq;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end verification of the C1-C5 wave against a real Roslyn pipeline.
/// Distinct from the per-commit unit tests — these tests:
/// <list type="bullet">
///   <item>Build a real on-disk csproj + .cs files.</item>
///   <item>Run the full <see cref="RoslynWorkspaceAnalyzer.AnalyzeWorkspace"/>
///         pipeline (not in-memory <see cref="GraphBuilder"/>).</item>
///   <item>Reproduce the exact dogfood scenario from the user's R2-3 report:
///         a graph with BOTH <c>FieldMask.ShimmerPhase</c> (enum member)
///         AND <c>BurstVoiceState.ShimmerPhase</c> (struct field) at the
///         same time, and assert the resolver picks the right one.</item>
///   <item>Parse the actual MCP wire JSON — not <see cref="GraphSession"/>
///         internal record — to confirm the <c>requestedMode</c> /
///         <c>fallbackReason</c> / <c>canRetryFull</c> / <c>suggestedRetry</c>
///         contract holds end-to-end.</item>
/// </list>
/// </summary>
public class WaveFunctionalVerificationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _temp;
    private readonly PhysicalFileSystem _fs = new();

    public WaveFunctionalVerificationTests(ITestOutputHelper output)
    {
        _output = output;
        _temp = Path.Combine(Path.GetTempPath(), $"lb-wave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Wave_R2_3_DogfoodReproduction_FieldMaskAndBurstVoiceStateShareShortName()
    {
        // The actual scenario from the user's R2-3 report. Two distinct
        // symbols share the short name "ShimmerPhase" across different
        // containing types. Pre-fix, asking for FieldMask.ShimmerPhase
        // silently returned BurstVoiceState.ShimmerPhase. Post-fix:
        //   - C1: enum member is in the graph, exact-ID lookup hits Rule 1.
        //   - C2: even if Rule 1 missed, Rule 4 would refuse cross-type sub.
        WriteCsproj();
        WriteSource("Code.cs", @"
namespace Demo;
public enum FieldMask { ShimmerPhase, BellCutoff, FilterEnv }
public struct BurstVoiceState { public int ShimmerPhase; }
");
        var graph = new RoslynWorkspaceAnalyzer(_fs)
            .AnalyzeWorkspace(_temp, new AnalysisConfig { RetainCompilations = true });

        // Both symbols must be in the graph.
        Assert.NotNull(graph.GetSymbol("field:Demo.FieldMask.ShimmerPhase"));
        Assert.NotNull(graph.GetSymbol("field:Demo.BurstVoiceState.ShimmerPhase"));
        _output.WriteLine("Both ShimmerPhase symbols present in graph");

        var resolver = new LifebloodSymbolResolver();

        // Exact ID for the enum member must NOT silently substitute the struct field.
        var enumLookup = resolver.Resolve(graph, "field:Demo.FieldMask.ShimmerPhase");
        Assert.Equal(ResolveOutcome.ExactMatch, enumLookup.Outcome);
        Assert.Equal("field:Demo.FieldMask.ShimmerPhase", enumLookup.CanonicalId);
        _output.WriteLine($"Enum-member exact lookup: outcome={enumLookup.Outcome}, id={enumLookup.CanonicalId}");

        // Exact ID for the struct field must resolve to the struct field.
        var structLookup = resolver.Resolve(graph, "field:Demo.BurstVoiceState.ShimmerPhase");
        Assert.Equal(ResolveOutcome.ExactMatch, structLookup.Outcome);
        Assert.Equal("field:Demo.BurstVoiceState.ShimmerPhase", structLookup.CanonicalId);
        _output.WriteLine($"Struct-field exact lookup: outcome={structLookup.Outcome}, id={structLookup.CanonicalId}");

        // Cross-type query against a non-existent containing type must NotFound
        // with both real candidates surfaced as Did-you-mean.
        var crossType = resolver.Resolve(graph, "field:Demo.NonExistent.ShimmerPhase");
        Assert.Equal(ResolveOutcome.NotFound, crossType.Outcome);
        Assert.Contains("field:Demo.FieldMask.ShimmerPhase", crossType.Candidates);
        Assert.Contains("field:Demo.BurstVoiceState.ShimmerPhase", crossType.Candidates);
        _output.WriteLine($"Cross-type lookup: NotFound + 2 candidates (both real ShimmerPhase)");
    }

    [Fact]
    public void Wave_C3_WireShape_AllFiveStatesEndToEnd()
    {
        // Drive the GraphSession through every wire-shape state the new
        // contract introduces, parse the JSON each time, and assert the
        // exact field shape an MCP client would observe.
        WriteCsproj();
        WriteSource("A.cs", "namespace D; public class A { }");

        var session = new GraphSession(_fs);

        // State 1: full analyze (user explicitly asked for full)
        var full = JsonDocument.Parse(session.Load(_temp, null, null, incremental: false));
        Assert.Equal("full", full.RootElement.GetProperty("mode").GetString());
        Assert.Equal("full", full.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(full.RootElement, "fallbackReason");
        AssertNullProperty(full.RootElement, "canRetryFull");
        _output.WriteLine("State 1 (full): mode=full, requestedMode=full, no fallback");

        // State 2: incremental no-op (no edits since last analyze)
        var noop = JsonDocument.Parse(session.Load(_temp, null, null, incremental: true));
        Assert.Equal("incremental-noop", noop.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", noop.RootElement.GetProperty("requestedMode").GetString());
        AssertNullProperty(noop.RootElement, "fallbackReason");
        _output.WriteLine("State 2 (incremental-noop): mode=incremental-noop, requestedMode=incremental");

        // State 3: incremental after edit
        Thread.Sleep(50);
        File.AppendAllText(Path.Combine(_temp, "A.cs"), "\n// edit\n");
        var inc = JsonDocument.Parse(session.Load(_temp, null, null, incremental: true));
        Assert.Equal("incremental", inc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", inc.RootElement.GetProperty("requestedMode").GetString());
        Assert.True(inc.RootElement.GetProperty("changedSourceFiles").GetInt32() > 0);
        _output.WriteLine($"State 3 (incremental): changedSourceFiles={inc.RootElement.GetProperty("changedSourceFiles").GetInt32()}");
    }

    [Fact]
    public void Wave_C3_WireShape_RejectedAndFullFallback_ModuleSetDrift()
    {
        // The two states that depend on caller-controlled drift policy.
        // Build a two-module workspace, analyze, drop one module, then
        // exercise both the Rejected and FullFallback wire shapes.
        Directory.CreateDirectory(Path.Combine(_temp, "MA"));
        Directory.CreateDirectory(Path.Combine(_temp, "MB"));
        var csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_temp, "MA", "MA.csproj"), csproj);
        File.WriteAllText(Path.Combine(_temp, "MB", "MB.csproj"), csproj);
        File.WriteAllText(Path.Combine(_temp, "MA", "A.cs"), "namespace MA; public class A { }");
        File.WriteAllText(Path.Combine(_temp, "MB", "B.cs"), "namespace MB; public class B { }");

        // Sub-test 1: AllowFullFallback=false → mode:rejected with full retry envelope
        var session1 = new GraphSession(_fs);
        session1.Load(_temp, null, null, incremental: false);
        File.Delete(Path.Combine(_temp, "MB", "MB.csproj"));
        File.Delete(Path.Combine(_temp, "MB", "B.cs"));

        var rej = JsonDocument.Parse(session1.Load(_temp, null, null,
                                                   incremental: true,
                                                   allowFullFallback: false));

        Assert.Equal("rejected", rej.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", rej.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", rej.RootElement.GetProperty("fallbackReason").GetString());
        Assert.True(rej.RootElement.GetProperty("canRetryFull").GetBoolean());
        var sug = rej.RootElement.GetProperty("suggestedRetry");
        Assert.True(sug.GetProperty("incremental").GetBoolean());
        Assert.True(sug.GetProperty("allowFullFallback").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rej.RootElement.GetProperty("summary").ValueKind);
        _output.WriteLine("State 4 (rejected): full envelope, no work done, suggestedRetry populated");

        // Sub-test 2: AllowFullFallback=true → mode:full + requestedMode:incremental
        // Re-set up: rebuild the second module and full-analyze fresh.
        File.WriteAllText(Path.Combine(_temp, "MB", "MB.csproj"), csproj);
        File.WriteAllText(Path.Combine(_temp, "MB", "B.cs"), "namespace MB; public class B { }");
        var session2 = new GraphSession(_fs);
        session2.Load(_temp, null, null, incremental: false);
        File.Delete(Path.Combine(_temp, "MB", "MB.csproj"));
        File.Delete(Path.Combine(_temp, "MB", "B.cs"));

        var ff = JsonDocument.Parse(session2.Load(_temp, null, null,
                                                  incremental: true,
                                                  allowFullFallback: true));

        Assert.Equal("full", ff.RootElement.GetProperty("mode").GetString());
        Assert.Equal("incremental", ff.RootElement.GetProperty("requestedMode").GetString());
        Assert.Equal("moduleSetChanged", ff.RootElement.GetProperty("fallbackReason").GetString());
        AssertNullProperty(ff.RootElement, "canRetryFull");
        Assert.NotEqual(JsonValueKind.Null, ff.RootElement.GetProperty("summary").ValueKind);
        _output.WriteLine("State 5 (full-fallback): mode=full + requestedMode=incremental + fallbackReason populated");
    }

    [Fact]
    public void Wave_C4_MatchKind_AllFourValuesAchievable()
    {
        // Real graph with xmldoc-summary properties to exercise all four
        // MatchKind values through the actual extraction pipeline.
        WriteCsproj();
        WriteSource("Code.cs", @"
namespace App.Domain;

/// <summary>Canonicalizes user input before persisting.</summary>
public class UserRepository { public string Id { get; set; } = """"; }

/// <summary>Normalizes input strings.</summary>
public class NormalizeService { public void Normalize() { } }

public class UnrelatedThing { }
");
        var graph = new RoslynWorkspaceAnalyzer(_fs)
            .AnalyzeWorkspace(_temp, new AnalysisConfig());
        var search = new LifebloodSemanticSearchProvider();

        // Name only — query "UserRepository" hits bare name only
        var nameHit = search.Search(graph, new SearchQuery("UserRepository"))
                            .First(r => r.CanonicalId == "type:App.Domain.UserRepository");
        Assert.Equal(MatchKind.Name, nameHit.MatchKind);
        _output.WriteLine($"MatchKind.Name confirmed for {nameHit.CanonicalId}");

        // QualifiedName only — query "Domain" hits FQN, not bare name
        var qnameHit = search.Search(graph, new SearchQuery("Domain"))
                             .First(r => r.CanonicalId == "type:App.Domain.UnrelatedThing");
        Assert.Equal(MatchKind.QualifiedName, qnameHit.MatchKind);
        _output.WriteLine($"MatchKind.QualifiedName confirmed for {qnameHit.CanonicalId}");

        // XmlDoc only — query "canonicaliz" only in the doc, not the name
        var xmlHit = search.Search(graph, new SearchQuery("canonicaliz"))
                           .First(r => r.CanonicalId == "type:App.Domain.UserRepository");
        Assert.Equal(MatchKind.XmlDoc, xmlHit.MatchKind);
        _output.WriteLine($"MatchKind.XmlDoc confirmed for {xmlHit.CanonicalId}");

        // Multiple — query "Normalize" hits BOTH bare name and xmldoc
        var multiHit = search.Search(graph, new SearchQuery("Normalize"))
                             .First(r => r.CanonicalId == "type:App.Domain.NormalizeService");
        Assert.Equal(MatchKind.Multiple, multiHit.MatchKind);
        _output.WriteLine($"MatchKind.Multiple confirmed for {multiHit.CanonicalId}");
    }

    private static void AssertNullProperty(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var p),
            $"Property '{name}' missing from response.");
        Assert.Equal(JsonValueKind.Null, p.ValueKind);
    }

    private void WriteCsproj()
    {
        File.WriteAllText(Path.Combine(_temp, "TestProject.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
    }

    private void WriteSource(string filename, string content)
    {
        File.WriteAllText(Path.Combine(_temp, filename), content);
    }
}
