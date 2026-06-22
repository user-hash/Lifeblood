using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-AUTHORITY-COVERAGE-001. Negative-dependency matrix: subject methods,
/// types, or files should reach the intended authority symbols by outgoing
/// graph paths.
/// </summary>
public class AuthorityCoverageAnalyzerTests
{
    [Fact]
    public void Analyze_DirectReach_ReportsRequiredAuthorityReached()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .AddEdge(new Edge
            {
                SourceId = "method:App.Generator.Generate()",
                TargetId = "field:App.State.InstrumentPresets",
                Kind = EdgeKind.References,
            })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("method:App.Generator.Generate()") },
            required: new[] { Sym("field:App.State.InstrumentPresets") }));

        var row = Assert.Single(report.Rows);
        Assert.Equal(AuthorityCoverageStatus.RequiredReached, row.Status);
        Assert.True(row.HasAllRequiredAuthority);
        Assert.Empty(row.MissingAuthorities);
        var reach = Assert.Single(row.ReachedAuthorities);
        Assert.Equal(1, reach.Distance);
        Assert.Equal(new[] { "method:App.Generator.Generate()", "field:App.State.InstrumentPresets" },
            reach.Path.Select(p => p.SymbolId).ToArray());
    }

    [Fact]
    public void Analyze_TransitiveReach_CarriesShortestPathPreview()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .AddSymbol(Type("type:App.Policy", "Policy", "src/Policy.cs"))
            .AddSymbol(Method("method:App.Policy.Read()", "Read", "type:App.Policy", "src/Policy.cs"))
            .AddEdge(new Edge { SourceId = "method:App.Generator.Generate()", TargetId = "method:App.Policy.Read()", Kind = EdgeKind.Calls })
            .AddEdge(new Edge { SourceId = "method:App.Policy.Read()", TargetId = "field:App.State.InstrumentPresets", Kind = EdgeKind.References })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("method:App.Generator.Generate()") },
            required: new[] { Sym("field:App.State.InstrumentPresets") }));

        var reach = Assert.Single(Assert.Single(report.Rows).ReachedAuthorities);
        Assert.Equal(2, reach.Distance);
        Assert.Equal(new[] { "", "Calls", "References" }, reach.Path.Select(p => p.ViaEdgeKind).ToArray());
    }

    [Fact]
    public void Analyze_MissingAuthority_ReportsMissingRequired()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("method:App.Generator.Generate()") },
            required: new[] { Sym("field:App.State.InstrumentPresets") }));

        var row = Assert.Single(report.Rows);
        Assert.Equal(AuthorityCoverageStatus.MissingRequired, row.Status);
        Assert.False(row.HasAllRequiredAuthority);
        Assert.Equal(new[] { "field:App.State.InstrumentPresets" }, row.MissingAuthorities);
    }

    [Fact]
    public void Analyze_AllowedAlternative_IsReportedAsCompetingAuthority()
    {
        var graph = BaseGraph()
            .AddSymbol(Field("field:App.Legacy.CurrentPreset", "CurrentPreset", "type:App.Legacy", "src/Legacy.cs"))
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .AddEdge(new Edge { SourceId = "method:App.Generator.Generate()", TargetId = "field:App.Legacy.CurrentPreset", Kind = EdgeKind.References })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("method:App.Generator.Generate()") },
            required: new[] { Sym("field:App.State.InstrumentPresets") },
            alternatives: new[] { Sym("field:App.Legacy.CurrentPreset") }));

        var row = Assert.Single(report.Rows);
        Assert.Equal(AuthorityCoverageStatus.AllowedAlternativeReached, row.Status);
        Assert.Equal(new[] { "field:App.State.InstrumentPresets" }, row.MissingAuthorities);
        Assert.NotNull(row.FirstCompetingAuthority);
        Assert.Equal("field:App.Legacy.CurrentPreset", row.FirstCompetingAuthority!.AuthorityId);
    }

    [Fact]
    public void Analyze_TypeSubject_ExpandsToContainedMethods()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .AddEdge(new Edge { SourceId = "method:App.Generator.Generate()", TargetId = "field:App.State.InstrumentPresets", Kind = EdgeKind.References })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("type:App.Generator") },
            required: new[] { Sym("field:App.State.InstrumentPresets") }));

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ExpandedSubjectCount);
        Assert.Equal(new[] { "method:App.Generator.Generate()" }, row.SubjectSeedPreview);
        Assert.True(row.HasAllRequiredAuthority);
    }

    [Fact]
    public void Analyze_FileSubject_ExpandsToMethodsInThatFile()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Generator.Generate()", "Generate", "type:App.Generator", "src/Generator.cs"))
            .AddEdge(new Edge { SourceId = "method:App.Generator.Generate()", TargetId = "field:App.State.InstrumentPresets", Kind = EdgeKind.References })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { FileInput("src/Generator.cs") },
            required: new[] { Sym("field:App.State.InstrumentPresets") }));

        var row = Assert.Single(report.Rows);
        Assert.Equal("File", row.SubjectKind);
        Assert.Equal(new[] { "method:App.Generator.Generate()" }, row.SubjectSeedPreview);
        Assert.True(row.HasAllRequiredAuthority);
    }

    [Fact]
    public void Analyze_ExcludeTests_DropsExpandedSubjectSeeds()
    {
        var graph = BaseGraph()
            .AddSymbol(Method("method:App.Tests.GeneratorTests.Generate()", "Generate", "type:App.Tests.GeneratorTests", "tests/GeneratorTests.cs"))
            .AddEdge(new Edge { SourceId = "method:App.Tests.GeneratorTests.Generate()", TargetId = "field:App.State.InstrumentPresets", Kind = EdgeKind.References })
            .Build();

        var report = AuthorityCoverageAnalyzer.Analyze(graph, Options(
            subjects: new[] { Sym("method:App.Tests.GeneratorTests.Generate()") },
            required: new[] { Sym("field:App.State.InstrumentPresets") },
            excludeTests: true));

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ExpandedSubjectCount);
        Assert.Equal(0, row.AnalyzedSubjectSeedCount);
        Assert.Equal(AuthorityCoverageStatus.NoSubjectSeeds, row.Status);
    }

    private static GraphBuilder BaseGraph()
        => new GraphBuilder()
            .AddSymbol(Type("type:App.Generator", "Generator", "src/Generator.cs"))
            .AddSymbol(Type("type:App.State", "State", "src/State.cs"))
            .AddSymbol(Type("type:App.Legacy", "Legacy", "src/Legacy.cs"))
            .AddSymbol(Type("type:App.Tests.GeneratorTests", "GeneratorTests", "tests/GeneratorTests.cs"))
            .AddSymbol(Field("field:App.State.InstrumentPresets", "InstrumentPresets", "type:App.State", "src/State.cs"));

    private static AuthorityCoverageOptions Options(
        AuthorityCoverageInput[] subjects,
        AuthorityCoverageInput[] required,
        AuthorityCoverageInput[]? alternatives = null,
        bool excludeTests = false)
        => new()
        {
            Subjects = subjects,
            RequiredAuthorities = required,
            AllowedAlternatives = alternatives ?? Array.Empty<AuthorityCoverageInput>(),
            ExcludeTests = excludeTests,
            MaxDepth = 6,
        };

    private static AuthorityCoverageInput Sym(string id)
        => new()
        {
            Input = id,
            Id = id,
            Kind = AuthorityCoverageInputKind.Symbol,
        };

    private static AuthorityCoverageInput FileInput(string path)
        => new()
        {
            Input = path,
            Id = path,
            Kind = AuthorityCoverageInputKind.File,
            FilePath = path,
        };

    private static Symbol Type(string id, string name, string filePath)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = id.StartsWith("type:", StringComparison.Ordinal) ? id.Substring(5) : id,
            Kind = SymbolKind.Type,
            FilePath = filePath,
        };

    private static Symbol Method(string id, string name, string parentId, string filePath)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = id.StartsWith("method:", StringComparison.Ordinal) ? id.Substring(7) : id,
            Kind = SymbolKind.Method,
            ParentId = parentId,
            FilePath = filePath,
        };

    private static Symbol Field(string id, string name, string parentId, string filePath)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = id.StartsWith("field:", StringComparison.Ordinal) ? id.Substring(6) : id,
            Kind = SymbolKind.Field,
            ParentId = parentId,
            FilePath = filePath,
        };
}
