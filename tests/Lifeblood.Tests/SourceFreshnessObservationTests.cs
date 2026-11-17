using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Workspaces;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SourceFreshnessObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lifeblood-freshness-{Guid.NewGuid():N}");
    private readonly PhysicalFileSystem _fs = new();

    public SourceFreshnessObservationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CappedObservation_ExposesUncheckedTailAndNeverClaimsFresh()
    {
        var paths = Enumerable.Range(0, 257).Select(i => Path.Combine(_root, $"Source{i}.cs")).ToArray();
        var analyzed = DateTime.UtcNow.AddMinutes(-1);
        foreach (var path in paths) { File.WriteAllText(path, "class C { }"); File.SetLastWriteTimeUtc(path, analyzed.AddMinutes(-1)); }
        File.SetLastWriteTimeUtc(paths[^1], analyzed.AddSeconds(1));
        var response = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = analyzed, TrackedFilePaths = paths, FileSystem = _fs, FileScanLimit = 256,
        });
        Assert.Equal(0, response.FilesChangedSinceAnalyze);
        Assert.Equal("incomplete", response.Freshness!.Status);
        Assert.Equal(256, response.Freshness.CheckedFileCount);
        Assert.Equal(1, response.Freshness.UncheckedFileCount);
        Assert.Contains("incomplete: 256/257", response.Freshness.Summary);
        Assert.Contains("Content not compared", response.Freshness.Summary);
        Assert.Contains("Confidence describes publication semantics, not current inputs", response.Freshness.Summary);
        Assert.DoesNotContain(response.Limitations, l => l.Contains("Source observation"));
        var complete = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = analyzed, TrackedFilePaths = paths, FileSystem = _fs,
        });
        Assert.Equal(1, complete.FilesChangedSinceAnalyze);
        Assert.Equal("driftObserved", complete.Freshness!.Status);
    }

    [Fact]
    public void DeletedSource_IsObservedDrift()
    {
        var response = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = DateTime.UtcNow, TrackedFilePaths = new[] { Path.Combine(_root, "Deleted.cs") }, FileSystem = _fs,
        });
        Assert.Equal(1, response.FilesChangedSinceAnalyze);
        Assert.Equal(1, response.Freshness!.MissingFileCount);
        Assert.Equal("driftObserved", response.Freshness.Status);
    }

    [Fact]
    public void TimestampPreservingEdit_IsExplicitlyNotContentVerified()
    {
        var path = Path.Combine(_root, "Source.cs");
        var old = DateTime.UtcNow.AddHours(-2);
        File.WriteAllText(path, "class Changed { }");
        File.SetLastWriteTimeUtc(path, old);
        var response = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = old.AddMinutes(1), TrackedFilePaths = new[] { path }, FileSystem = _fs,
        });
        Assert.Equal("noDriftObserved", response.Freshness!.Status);
        Assert.False(response.Freshness.ContentCompared);
        Assert.False(response.Freshness.InputInventoryRecaptured);
        using var wire = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Contains("noDriftObserved: 1/1", wire.RootElement.GetProperty("freshness").GetProperty("summary").GetString());
        Assert.Contains("Input inventory not recaptured", response.Freshness.Summary);
        Assert.Contains(response.Limitations, l => l.Contains("Age alone does not establish source drift"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingTimestampOrFileSystem_IsUnavailable(bool noTimestamp)
    {
        var response = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = noTimestamp ? null : DateTime.UtcNow,
            FileSystem = noTimestamp ? _fs : null,
            TrackedFilePaths = new[] { "Source.cs" },
        });
        Assert.Equal("unavailable", response.Freshness!.Status);
        Assert.Equal(0, response.Freshness.CheckedFileCount);
        Assert.Equal(1, response.Freshness.UncheckedFileCount);
        Assert.Contains("unavailable: 0/1", response.Freshness.Summary);
        Assert.Contains(response.Limitations, l => l.Contains("Source observation is unavailable"));
    }

    [Fact]
    public void FailedObservation_WarnsEvenWhenOtherSourcesWereChecked()
    {
        var path = Path.Combine(_root, "Unchanged.cs");
        File.WriteAllText(path, "class C { }");
        var response = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = DateTime.UtcNow,
            TrackedFilePaths = new[] { path, "" }, FileSystem = _fs,
        });
        Assert.Equal("incomplete", response.Freshness!.Status);
        Assert.Equal(1, response.Freshness.UnavailableFileCount);
        Assert.Contains(response.Limitations, l => l.Contains("1 unavailable"));
    }

    [Fact]
    public void Inventory_IsImmutableAndSharedWithHistoricalReplica()
    {
        var graph = new GraphBuilder().AddSymbol(new Symbol { Id = "file:A.cs", Name = "A.cs", Kind = SymbolKind.File, FilePath = "A.cs" }).Build();
        var supplied = new[] { Path.Combine(_root, "A.cs") };
        using var snapshot = WorkspaceSnapshot.Create(graph, new AnalysisResult(), null, "test", new WorkspaceContext(_root), DateTime.UtcNow, 1, trackedFilePaths: supplied);
        supplied[0] = "Mutated.cs";
        using var replica = snapshot.CreateGraphOnlyReplica();
        Assert.Same(snapshot.TrackedFilePaths, replica.TrackedFilePaths);
        Assert.Equal(Path.Combine(_root, "A.cs"), Assert.Single(snapshot.TrackedFilePaths));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.TrackedFilePaths).Add("B.cs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalPackageInventory_UsesAdapterPhysicalInputs(bool readOnly)
    {
        var project = Path.Combine(_root, "Workspace");
        var external = Path.Combine(_root, "External");
        Directory.CreateDirectory(Path.Combine(project, "Library"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        Directory.CreateDirectory(Path.Combine(external, "Runtime"));
        File.WriteAllText(Path.Combine(external, "package.json"), """{"name":"com.probe.external","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(external, "Runtime", "Probe.asmdef"), """{"name":"Probe"}""");
        var source = Path.Combine(external, "Runtime", "External.cs");
        File.WriteAllText(source, "namespace Probe; public class External { }");
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), """{"dependencies":{"com.probe.external":"file:../External"}}""");
        File.WriteAllText(Path.Combine(project, "Probe.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
              <ItemGroup><Compile Include="../External/Runtime/External.cs" /></ItemGroup>
            </Project>
            """);
        using var session = new GraphSession(_fs);
        using var load = System.Text.Json.JsonDocument.Parse(session.Load(project, null, null, readOnly: readOnly));
        Assert.Equal("full", load.RootElement.GetProperty("mode").GetString());
        Assert.Contains(session.Graph!.Symbols, s => s.FilePath == "Packages/com.probe.external/Runtime/External.cs");
        Assert.Equal(Path.GetFullPath(source), Assert.Single(session.CurrentSnapshot.TrackedFilePaths));
        var observation = new LifebloodResponseDecorator().Decorate("lifeblood_lookup", new EnvelopeContext
        {
            AnalyzedAtUtc = session.AnalyzedAtUtc, TrackedFilePaths = session.CurrentSnapshot.TrackedFilePaths, FileSystem = _fs,
        });
        Assert.Equal("noDriftObserved", observation.Freshness!.Status);
        Assert.Equal(0, observation.Freshness.MissingFileCount);
        if (!readOnly)
        {
            File.WriteAllText(source, "namespace Probe; public class Changed { }");
            using var increment = System.Text.Json.JsonDocument.Parse(session.Load(project, null, null, incremental: true));
            Assert.Equal("incremental", increment.RootElement.GetProperty("mode").GetString());
            Assert.Equal(Path.GetFullPath(source), Assert.Single(session.CurrentSnapshot.TrackedFilePaths));
        }
    }
}
