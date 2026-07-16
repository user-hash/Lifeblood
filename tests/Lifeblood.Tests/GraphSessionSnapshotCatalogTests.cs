using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class GraphSessionSnapshotCatalogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"lifeblood-snapshot-catalog-{Guid.NewGuid():N}");

    public GraphSessionSnapshotCatalogTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public void ReloadSameWorkspace_RetainsExactGraphOnlyPublication()
    {
        var graphPath = WriteGraph(Path.Combine(_tempDir, "Workspace"), "First");
        using var session = new GraphSession(
            new PhysicalFileSystem(),
            snapshotCatalog: new WorkspaceSnapshotCatalog(
                new WorkspaceSnapshotCatalogOptions(2, TimeSpan.Zero)));
        session.Load(projectPath: null, graphPath, rulesPath: null);
        var firstSnapshot = session.CurrentSnapshot;

        WriteGraph(Path.GetDirectoryName(graphPath)!, "Second");
        session.Load(projectPath: null, graphPath, rulesPath: null);
        var latestSnapshot = session.CurrentSnapshot;

        Assert.NotEqual(firstSnapshot.SnapshotId, latestSnapshot.SnapshotId);
        var retained = Assert.Single(session.SnapshotHistory);
        Assert.Equal(firstSnapshot.SnapshotId, retained.Snapshot.SnapshotId);
        Assert.False(retained.Snapshot.RetainsSemanticServices);

        using (session.AcquireReadLease(firstSnapshot.SnapshotId))
        {
            Assert.Equal(firstSnapshot.SnapshotId, session.SnapshotId);
            Assert.True(session.IsHistoricalSelection);
            Assert.NotNull(session.Graph?.GetSymbol("type:Fixture.First"));
            Assert.Null(session.Graph?.GetSymbol("type:Fixture.Second"));
            Assert.False(session.HasCompilationState);
        }

        Assert.Equal(latestSnapshot.SnapshotId, session.SnapshotId);
        Assert.False(session.IsHistoricalSelection);
        Assert.NotNull(session.Graph?.GetSymbol("type:Fixture.Second"));
    }

    [Fact]
    public void CrossWorkspaceReload_ClearsHistoricalCatalog()
    {
        var firstPath = WriteGraph(Path.Combine(_tempDir, "FirstWorkspace"), "First");
        var secondPath = WriteGraph(Path.Combine(_tempDir, "SecondWorkspace"), "Second");
        using var session = new GraphSession(new PhysicalFileSystem());

        session.Load(projectPath: null, firstPath, rulesPath: null);
        WriteGraph(Path.GetDirectoryName(firstPath)!, "FirstUpdated");
        session.Load(projectPath: null, firstPath, rulesPath: null);
        Assert.Single(session.SnapshotHistory);

        session.Load(projectPath: null, secondPath, rulesPath: null);

        Assert.Empty(session.SnapshotHistory);
        Assert.NotNull(session.Graph?.GetSymbol("type:Fixture.Second"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string WriteGraph(string root, string typeName)
    {
        Directory.CreateDirectory(root);
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "mod:Fixture", Name = "Fixture", Kind = SymbolKind.Module })
            .AddSymbol(new Symbol
            {
                Id = $"type:Fixture.{typeName}",
                Name = typeName,
                Kind = SymbolKind.Type,
                ParentId = "mod:Fixture",
                FilePath = $"{typeName}.cs",
                Line = 1,
            })
            .Build();
        var document = new GraphDocument
        {
            Language = "test",
            Adapter = new AdapterCapability
            {
                CanDiscoverSymbols = true,
                TypeResolution = ConfidenceLevel.Proven,
            },
            Graph = graph,
        };
        var path = Path.Combine(root, "graph.json");
        using var stream = File.Create(path);
        new JsonGraphExporter().Export(document, stream);
        return path;
    }
}
