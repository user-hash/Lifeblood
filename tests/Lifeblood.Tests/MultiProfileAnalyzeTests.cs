using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MULTI-DEFINE-ANALYZE-001 + INV-MULTI-DEFINE-EDGE-PROFILES-001.</summary>
public class MultiProfileAnalyzeTests
{
    [Fact]
    public void DefineProfileApplier_AppliesAddAndRemove_OrdinalSortedDistinct()
    {
        var profile = new DefineProfile
        {
            Name = "Player",
            AddDefines = new[] { "ENABLE_IL2CPP", "ENABLE_IL2CPP" },
            RemoveDefines = new[] { "UNITY_EDITOR" },
        };
        var baseSymbols = new[] { "UNITY_2023", "UNITY_EDITOR", "DEBUG" };

        var active = DefineProfileApplier.Apply(baseSymbols, profile);

        Assert.Equal(new[] { "DEBUG", "ENABLE_IL2CPP", "UNITY_2023" }, active);
    }

    [Fact]
    public void DefineProfileApplier_IdentityProfile_PreservesBaseline()
    {
        var profile = new DefineProfile
        {
            Name = "Editor",
            AddDefines = Array.Empty<string>(),
            RemoveDefines = Array.Empty<string>(),
        };
        var baseSymbols = new[] { "DEBUG", "UNITY_EDITOR" };

        var active = DefineProfileApplier.Apply(baseSymbols, profile);

        Assert.Equal(new[] { "DEBUG", "UNITY_EDITOR" }, active);
    }

    [Fact]
    public void EdgeProfileTagger_NullProfile_ReturnsEdgesUntagged()
    {
        var edges = new List<Edge>
        {
            new() { SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls },
        };

        var tagged = EdgeProfileTagger.Tag(edges, null);

        Assert.Single(tagged);
        Assert.Null(tagged[0].Profiles);
    }

    [Fact]
    public void EdgeProfileTagger_NamedProfile_AttachesProfileNameAsSingletonList()
    {
        var edges = new List<Edge>
        {
            new() { SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls },
            new() { SourceId = "C", TargetId = "D", Kind = EdgeKind.References },
        };

        var tagged = EdgeProfileTagger.Tag(edges, "Player");

        Assert.Equal(2, tagged.Count);
        Assert.All(tagged, e =>
        {
            Assert.NotNull(e.Profiles);
            Assert.Equal(new[] { "Player" }, e.Profiles);
        });
    }

    [Fact]
    public void EdgeProfileMerger_TwoProfilesSameEdge_UnionsProfilesOrdinalSorted()
    {
        var existing = new Edge
        {
            SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls,
            Profiles = new[] { "Editor" },
        };
        var incoming = new Edge
        {
            SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls,
            Profiles = new[] { "Player" },
        };

        var merged = EdgeProfileMerger.MergeProfiles(existing, incoming);

        Assert.NotNull(merged.Profiles);
        Assert.Equal(new[] { "Editor", "Player" }, merged.Profiles);
    }

    [Fact]
    public void EdgeProfileMerger_IdenticalProfileSets_ReturnsExistingUnchanged()
    {
        var existing = new Edge
        {
            SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls,
            Profiles = new[] { "Editor", "Player" },
        };
        var incoming = new Edge
        {
            SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls,
            Profiles = new[] { "Editor", "Player" },
        };

        var merged = EdgeProfileMerger.MergeProfiles(existing, incoming);

        Assert.Same(existing, merged);
    }

    [Fact]
    public void EdgeProfileMerger_BothNullProfiles_ReturnsExistingUnchanged()
    {
        var existing = new Edge { SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls };
        var incoming = new Edge { SourceId = "A", TargetId = "B", Kind = EdgeKind.Calls };

        var merged = EdgeProfileMerger.MergeProfiles(existing, incoming);

        Assert.Same(existing, merged);
        Assert.Null(merged.Profiles);
    }

    [Fact]
    public void GraphBuilder_UnionDeduped_TwoProfilesSameEdge_KeepsOneEdgeWithBothProfiles()
    {
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = Domain.Graph.SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = Domain.Graph.SymbolKind.Type })
            .AddEdge(new Edge
            {
                SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Calls,
                Profiles = new[] { "Editor" },
            })
            .AddEdge(new Edge
            {
                SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Calls,
                Profiles = new[] { "Player" },
            });

        var graph = builder.Build();

        var matched = graph.Edges
            .Where(e => e.SourceId == "type:A" && e.TargetId == "type:B" && e.Kind == EdgeKind.Calls)
            .ToArray();
        Assert.Single(matched);
        Assert.NotNull(matched[0].Profiles);
        Assert.Equal(new[] { "Editor", "Player" }, matched[0].Profiles);
    }

    [Fact]
    public void GraphBuilder_SingleProfileBackCompat_EdgesKeepProfilesNull()
    {
        var builder = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:A", Name = "A", Kind = Domain.Graph.SymbolKind.Type })
            .AddSymbol(new Symbol { Id = "type:B", Name = "B", Kind = Domain.Graph.SymbolKind.Type })
            .AddEdge(new Edge { SourceId = "type:A", TargetId = "type:B", Kind = EdgeKind.Calls });

        var graph = builder.Build();

        var matched = graph.Edges.Single(e => e.SourceId == "type:A" && e.TargetId == "type:B");
        Assert.Null(matched.Profiles);
    }

    [Fact]
    public void AnalysisConfig_DefineProfiles_DefaultsToNull_BackCompatSingleProfile()
    {
        var config = new AnalysisConfig();
        Assert.Null(config.DefineProfiles);
    }

    [Fact]
    public void ResolveActiveProfiles_UnknownProfileName_ThrowsArgumentException()
    {
        var fs = new PhysicalFileSystem();
        var analyzer = new RoslynWorkspaceAnalyzer(fs);
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-mp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig { DefineProfiles = new[] { "Mars" } }));
            Assert.Contains("Mars", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
