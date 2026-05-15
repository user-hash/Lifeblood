using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// End-to-end ratchet wall for dispatch-table delegate liveness
/// (INV-EXTRACT-DISPATCH-TABLE-COVERAGE-001 closure of LB-INBOX-011
/// part 2). Builds a realistic dispatch-table fixture, runs Lifeblood's
/// full discovery + analyzer + graph pipeline, then verifies the two
/// graph-walk surfaces that historically misclassified delegate row
/// methods both see the targets as live:
///
///   - <see cref="LifebloodDeadCodeAnalyzer.FindDeadCode"/> never flags
///     a delegate row method as dead.
///   - <see cref="SemanticGraph.GetIncomingEdgeIndexes"/> on the
///     delegate row method returns at least one incoming edge.
///
/// <see cref="Lifeblood.Analysis.BlastRadiusAnalyzer"/> and
/// <see cref="Lifeblood.Connectors.Mcp.LifebloodPortHealthAnalyzer"/>
/// (and similar) walk the same <see cref="SemanticGraph"/> indexes the
/// dependants check exercises here — a regression in either would
/// require a regression in graph-edge emission first, which this
/// ratchet catches at its root.
/// </summary>
public class DispatchTableLivenessRatchetTests
{
    [Fact]
    public void DispatchTableDelegateTargets_AreLiveAcrossGraphWalkSurfaces()
    {
        using var tempDir = new ScratchDir();
        tempDir.WriteFile("Capability.cs", """
            namespace App;
            public class Capability
            {
                public Capability(Mode mode, System.Action handler) { }
            }
            """);
        tempDir.WriteFile("Mode.cs", "namespace App; public enum Mode { A, B }");
        tempDir.WriteFile("Registry.cs", """
            namespace App;
            public static class Registry
            {
                public static readonly Capability[] All =
                {
                    new Capability(Mode.A, HandleA),
                    new Capability(Mode.B, HandleB),
                };
                private static void HandleA() { }
                private static void HandleB() { }
            }
            """);
        tempDir.WriteFile("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>App</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem());
        var graph = analyzer.AnalyzeWorkspace(tempDir.Path, new AnalysisConfig());

        // Axis 1 — graph dependants. Each delegate row target must have
        // at least one incoming edge attributing the reference back to
        // the synthesized .cctor (the only place the table can be
        // initialized in C# semantics).
        var handleA = "method:App.Registry.HandleA()";
        var handleB = "method:App.Registry.HandleB()";
        Assert.True(graph.GetSymbol(handleA) != null, $"{handleA} missing from graph");
        Assert.True(graph.GetSymbol(handleB) != null, $"{handleB} missing from graph");
        Assert.True(graph.GetIncomingEdgeIndexes(handleA).Length >= 1,
            "HandleA has zero incoming edges — dispatch-table coverage regressed");
        Assert.True(graph.GetIncomingEdgeIndexes(handleB).Length >= 1,
            "HandleB has zero incoming edges — dispatch-table coverage regressed");

        // Axis 2 — dead_code. The analyzer walks Calls + References edges
        // via HasIncomingReference, so both delegate targets must drop
        // out of the findings list.
        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));
        Assert.DoesNotContain(findings, f => f.CanonicalId == handleA);
        Assert.DoesNotContain(findings, f => f.CanonicalId == handleB);

        // The synthesized .cctor that anchors the dispatch-table edges
        // must not be flagged either — the CLR always invokes it on
        // type init, so a finding here would be the same noise class
        // the surface exists to eliminate, shifted one symbol over.
        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:App.Registry..cctor()");
    }

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; }

        public ScratchDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"lifeblood-dispatch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void WriteFile(string relativePath, string content)
            => File.WriteAllText(System.IO.Path.Combine(Path, relativePath), content);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
