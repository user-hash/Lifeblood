using Lifeblood.Analysis;
using Lifeblood.Domain.Graph;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ASMDEF-CHECK-001. DirectOnly modules, such as Unity asmdef-generated
/// projects, must declare every cross-module source dependency directly.
/// </summary>
public class AsmdefBoundaryAnalyzerTests
{
    [Fact]
    public void Analyze_DirectOnlyMissingDeclaredReference_ReportsFirstOffendingEdge()
    {
        var graph = BaseGraph(declaredDependency: false, sourceClosure: "DirectOnly");

        var report = AsmdefBoundaryAnalyzer.Analyze(graph);

        Assert.Equal(2, report.ModuleCount);
        Assert.Equal(1, report.DirectOnlyModuleCount);
        Assert.Equal(1, report.CheckedCrossModuleEdgeCount);
        Assert.Equal(1, report.ViolationCount);

        var violation = Assert.Single(report.Violations);
        Assert.Equal("mod:Source", violation.SourceModuleId);
        Assert.Equal("Source", violation.SourceModuleName);
        Assert.Equal("mod:Target", violation.TargetModuleId);
        Assert.Equal("Target", violation.TargetModuleName);
        Assert.Equal(1, violation.OffendingEdgeCount);
        Assert.Equal("method:Source.Service.Run()", violation.SourceSymbolId);
        Assert.Equal("method:Target.Helper.Help()", violation.TargetSymbolId);
        Assert.Equal("Calls", violation.EdgeKind);
        Assert.Empty(violation.DeclaredDependencyModuleIds);
        Assert.NotNull(violation.CallSite);
        Assert.Equal("Source/Service.cs", violation.CallSite!.FilePath);
        Assert.Equal(12, violation.CallSite.Line);
        Assert.Equal(new[] { "Player" }, violation.Profiles);
    }

    [Fact]
    public void Analyze_DirectOnlyDeclaredReference_Passes()
    {
        var graph = BaseGraph(declaredDependency: true, sourceClosure: "DirectOnly");

        var report = AsmdefBoundaryAnalyzer.Analyze(graph);

        Assert.Equal(1, report.CheckedCrossModuleEdgeCount);
        Assert.Equal(0, report.ViolationCount);
        Assert.Empty(report.Violations);
        Assert.Equal(1, report.DeclaredModuleDependencyCount);
    }

    [Fact]
    public void Analyze_TransitiveSourceModule_IsSkipped()
    {
        var graph = BaseGraph(declaredDependency: false, sourceClosure: "Transitive");

        var report = AsmdefBoundaryAnalyzer.Analyze(graph);

        Assert.Equal(0, report.DirectOnlyModuleCount);
        Assert.Equal(2, report.SkippedModuleCount);
        Assert.Equal(0, report.CheckedCrossModuleEdgeCount);
        Assert.Equal(0, report.ViolationCount);
    }

    private static SemanticGraph BaseGraph(bool declaredDependency, string sourceClosure)
    {
        var builder = new GraphBuilder()
            .AddSymbol(Module("mod:Source", "Source", sourceClosure))
            .AddSymbol(Module("mod:Target", "Target", "Transitive"))
            .AddSymbol(Type("type:Source.Service", "Service", "mod:Source"))
            .AddSymbol(Type("type:Target.Helper", "Helper", "mod:Target"))
            .AddSymbol(Method("method:Source.Service.Run()", "Run", "type:Source.Service"))
            .AddSymbol(Method("method:Target.Helper.Help()", "Help", "type:Target.Helper"))
            .AddEdge(new Edge
            {
                SourceId = "method:Source.Service.Run()",
                TargetId = "method:Target.Helper.Help()",
                Kind = EdgeKind.Calls,
                CallSite = new CallSite
                {
                    FilePath = "Source/Service.cs",
                    Line = 12,
                    Column = 17,
                    EndLine = 12,
                    EndColumn = 23,
                    ContainingSymbolId = "method:Source.Service.Run()",
                },
                Profiles = new[] { "Player" },
            });

        if (declaredDependency)
            builder.AddEdge(new Edge { SourceId = "mod:Source", TargetId = "mod:Target", Kind = EdgeKind.DependsOn });

        return builder.Build();
    }

    private static Symbol Module(string id, string name, string referenceClosure)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = name,
            Kind = SymbolKind.Module,
            Properties = new Dictionary<string, string>
            {
                [SymbolPropertyKeys.ReferenceClosure] = referenceClosure,
            },
        };

    private static Symbol Type(string id, string name, string parentId)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = id.Substring("type:".Length),
            Kind = SymbolKind.Type,
            ParentId = parentId,
        };

    private static Symbol Method(string id, string name, string parentId)
        => new()
        {
            Id = id,
            Name = name,
            QualifiedName = id.Substring("method:".Length),
            Kind = SymbolKind.Method,
            ParentId = parentId,
        };
}
