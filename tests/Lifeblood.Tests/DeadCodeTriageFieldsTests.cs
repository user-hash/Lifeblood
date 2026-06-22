using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Capabilities;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.PathClassification;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-DEADCODE-TRIAGE-001 — every dead-code finding
/// carries <c>directDependants</c>, <c>bucket</c>, and <c>declarationOnly</c>
/// so a consumer can triage findings without re-walking the graph.
/// </summary>
public class DeadCodeTriageFieldsTests
{
    private static readonly Evidence Evidence = new()
    {
        Kind = EvidenceKind.Semantic,
        AdapterName = "test",
        Confidence = ConfidenceLevel.Proven,
    };

    [Theory]
    [InlineData("src/Production/Foo.cs",                       DeadCodeBucket.Production)]
    [InlineData("D:\\repo\\src\\Production\\Foo.cs",           DeadCodeBucket.Production)]
    [InlineData("tests/MyTests.cs",                            DeadCodeBucket.Test)]
    [InlineData("src/foo/MyTest.cs",                           DeadCodeBucket.Test)]
    [InlineData("D:/repo/Tests/Editor/CompTests.cs",           DeadCodeBucket.Test)]
    [InlineData("Assets/Editor/Tools/Bar.cs",                  DeadCodeBucket.Editor)]
    [InlineData("D:\\proj\\Assets\\Editor\\Tools\\Bar.cs",     DeadCodeBucket.Editor)]
    [InlineData("Assets/Foo.Generated.cs",                     DeadCodeBucket.Generated)]
    [InlineData("Assets/Generated/Schema.cs",                  DeadCodeBucket.Generated)]
    [InlineData("obj/Debug/net8.0/Foo.cs",                     DeadCodeBucket.Generated)]
    [InlineData("bin/Release/Bar.cs",                          DeadCodeBucket.Generated)]
    [InlineData("Packages/com.vendor.tool/Runtime/Foo.cs",     DeadCodeBucket.Vendored)]
    [InlineData("Assets/TextMesh Pro/Examples & Extras/Demo.cs", DeadCodeBucket.Vendored)]
    [InlineData("",                                            DeadCodeBucket.Production)]
    public void ClassifyBucket_PathPrefix_PicksMostSpecificSignal(string filePath, DeadCodeBucket expected)
    {
        // Generated wins over Editor wins over Test because the most
        // specific signal should be the most authoritative classification.
        // A file under Tests/Editor/ is a TEST (file convention) not an
        // Editor utility; a file under obj/ is GENERATED even if it
        // happens to end in *Tests.cs.
        // ClassifyBucket lives in Lifeblood.Domain.PathClassification.PathBucketClassifier
        // post-FOLLOWUP-005. Cast bridges the parallel wire enum DeadCodeBucket
        // (Application port surface) to the canonical PathBucket — integer
        // parity is pinned by PathBucketParityTests.
        Assert.Equal(expected, (DeadCodeBucket)PathBucketClassifier.Classify(filePath));
    }

    [Fact]
    public void FindDeadCode_AbstractMethod_FlagsDeclarationOnly()
    {
        // Symbol with IsAbstract=true must be flagged declarationOnly so
        // a consumer doesn't treat interface/abstract members as routine
        // cleanup candidates. Deleting one breaks every implementor.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Acme.IFoo", Name = "IFoo", Kind = SymbolKind.Type,
                FilePath = "src/IFoo.cs", Visibility = Visibility.Internal,
                IsAbstract = true,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:Acme.IFoo.Bar()", Name = "Bar", Kind = SymbolKind.Method,
                FilePath = "src/IFoo.cs", ParentId = "type:Acme.IFoo",
                Visibility = Visibility.Internal, IsAbstract = true,
            })
            .AddEdge(new Edge
            {
                SourceId = "type:Acme.IFoo", TargetId = "method:Acme.IFoo.Bar()",
                Kind = EdgeKind.Contains, Evidence = Evidence,
            })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        var barFinding = findings.FirstOrDefault(f => f.CanonicalId == "method:Acme.IFoo.Bar()");
        Assert.NotNull(barFinding);
        Assert.True(barFinding!.DeclarationOnly,
            "Abstract method must be flagged DeclarationOnly so callers know deleting it breaks every implementor.");
    }

    [Fact]
    public void FindDeadCode_ConcreteMethod_NotFlaggedDeclarationOnly()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Acme.Foo", Name = "Foo", Kind = SymbolKind.Type,
                FilePath = "src/Foo.cs", Visibility = Visibility.Internal,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:Acme.Foo.Bar()", Name = "Bar", Kind = SymbolKind.Method,
                FilePath = "src/Foo.cs", ParentId = "type:Acme.Foo",
                Visibility = Visibility.Internal, IsAbstract = false,
            })
            .AddEdge(new Edge
            {
                SourceId = "type:Acme.Foo", TargetId = "method:Acme.Foo.Bar()",
                Kind = EdgeKind.Contains, Evidence = Evidence,
            })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        var bar = Assert.Single(findings, f => f.CanonicalId == "method:Acme.Foo.Bar()");
        Assert.False(bar.DeclarationOnly);
    }

    [Fact]
    public void FindDeadCode_ClassicFinding_DirectDependantsIsZero()
    {
        // The analyzer drops any symbol with non-Contains incoming edges
        // via HasIncomingReference, so directDependants on every emitted
        // finding is 0 by construction. The field carries the value
        // anyway as forward-compatible signal for future relaxed criteria.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = "type:Acme.Foo", Name = "Foo", Kind = SymbolKind.Type,
                FilePath = "src/Foo.cs", Visibility = Visibility.Internal,
            })
            .AddSymbol(new Symbol
            {
                Id = "method:Acme.Foo.Bar()", Name = "Bar", Kind = SymbolKind.Method,
                FilePath = "src/Foo.cs", ParentId = "type:Acme.Foo",
                Visibility = Visibility.Internal,
            })
            .AddEdge(new Edge
            {
                SourceId = "type:Acme.Foo", TargetId = "method:Acme.Foo.Bar()",
                Kind = EdgeKind.Contains, Evidence = Evidence,
            })
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(0, f.DirectDependants));
    }

    [Fact]
    public void FindDeadCode_BucketReflectsFilePath()
    {
        // Drive the analyzer across four findings, one per bucket, and
        // assert each one's bucket field reflects the path classification.
        var graph = new GraphBuilder()
            .AddSymbol(SymInProduction("type:Acme.Prod", "Prod", SymbolKind.Type))
            .AddSymbol(SymInProduction("method:Acme.Prod.A()", "A", SymbolKind.Method))
            .AddSymbol(SymInTest("type:Acme.Spec", "Spec", SymbolKind.Type))
            .AddSymbol(SymInTest("method:Acme.Spec.B()", "B", SymbolKind.Method))
            .AddSymbol(SymInEditor("type:Acme.Tool", "Tool", SymbolKind.Type))
            .AddSymbol(SymInEditor("method:Acme.Tool.C()", "C", SymbolKind.Method))
            .AddSymbol(SymInGenerated("type:Acme.Gen", "Gen", SymbolKind.Type))
            .AddSymbol(SymInGenerated("method:Acme.Gen.D()", "D", SymbolKind.Method))
            .AddSymbol(SymInVendored("type:Acme.Vendor", "Vendor", SymbolKind.Type))
            .AddSymbol(SymInVendored("method:Acme.Vendor.E()", "E", SymbolKind.Method))
            .AddEdge(ContainsEdge("type:Acme.Prod",  "method:Acme.Prod.A()"))
            .AddEdge(ContainsEdge("type:Acme.Spec",  "method:Acme.Spec.B()"))
            .AddEdge(ContainsEdge("type:Acme.Tool",  "method:Acme.Tool.C()"))
            .AddEdge(ContainsEdge("type:Acme.Gen",   "method:Acme.Gen.D()"))
            .AddEdge(ContainsEdge("type:Acme.Vendor", "method:Acme.Vendor.E()"))
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(IncludeKinds: new[] { SymbolKind.Method },
                                ExcludePublic: false, ExcludeTests: false));

        Assert.Equal(DeadCodeBucket.Production, findings.Single(f => f.CanonicalId == "method:Acme.Prod.A()").Bucket);
        Assert.Equal(DeadCodeBucket.Test,       findings.Single(f => f.CanonicalId == "method:Acme.Spec.B()").Bucket);
        Assert.Equal(DeadCodeBucket.Editor,     findings.Single(f => f.CanonicalId == "method:Acme.Tool.C()").Bucket);
        Assert.Equal(DeadCodeBucket.Generated,  findings.Single(f => f.CanonicalId == "method:Acme.Gen.D()").Bucket);
        Assert.Equal(DeadCodeBucket.Vendored,   findings.Single(f => f.CanonicalId == "method:Acme.Vendor.E()").Bucket);
    }

    [Fact]
    public void FindDeadCode_InternalStaticConditionalOnlyType_BucketsScaffolding()
    {
        var typeId = "type:Acme.PackageAssert";
        var methodId = "method:Acme.PackageAssert.VerifyPackage()";
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = typeId, Name = "PackageAssert", Kind = SymbolKind.Type,
                FilePath = "src/PackageAssert.cs", Visibility = Visibility.Internal,
                IsStatic = true,
            })
            .AddSymbol(new Symbol
            {
                Id = methodId, Name = "VerifyPackage", Kind = SymbolKind.Method,
                FilePath = "src/PackageAssert.cs", ParentId = typeId,
                Visibility = Visibility.Internal, IsStatic = true,
                Properties = new System.Collections.Generic.Dictionary<string, string>
                {
                    [SymbolPropertyKeys.Attributes] = "Conditional",
                },
            })
            .AddEdge(ContainsEdge(typeId, methodId))
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        Assert.Equal(DeadCodeBucket.Scaffolding, findings.Single(f => f.CanonicalId == typeId).Bucket);
        var method = findings.Single(f => f.CanonicalId == methodId);
        Assert.Equal(DeadCodeBucket.Scaffolding, method.Bucket);
        Assert.Contains("scaffolding", method.Reason);
    }

    [Fact]
    public void FindDeadCode_InternalStaticConstStringOnlyType_BucketsScaffolding()
    {
        var typeId = "type:Acme.AudioCallbackSchedulerInvariant";
        var fieldId = "field:Acme.AudioCallbackSchedulerInvariant.Id";
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = typeId, Name = "AudioCallbackSchedulerInvariant", Kind = SymbolKind.Type,
                FilePath = "src/AudioCallbackSchedulerInvariant.cs", Visibility = Visibility.Internal,
                IsStatic = true,
            })
            .AddSymbol(new Symbol
            {
                Id = fieldId, Name = "Id", Kind = SymbolKind.Field,
                FilePath = "src/AudioCallbackSchedulerInvariant.cs", ParentId = typeId,
                Visibility = Visibility.Internal, IsStatic = true,
                Properties = new System.Collections.Generic.Dictionary<string, string>
                {
                    [SymbolPropertyKeys.FieldType] = "string",
                    [SymbolPropertyKeys.ConstantValue] = "INV-AUDIO-CALLBACK-SCHEDULER-001",
                },
            })
            .AddEdge(ContainsEdge(typeId, fieldId))
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        Assert.Equal(DeadCodeBucket.Scaffolding, findings.Single(f => f.CanonicalId == typeId).Bucket);
        Assert.Equal(DeadCodeBucket.Scaffolding, findings.Single(f => f.CanonicalId == fieldId).Bucket);
    }

    [Fact]
    public void FindDeadCode_InternalStaticTypeWithRealMethod_RemainsProduction()
    {
        var typeId = "type:Acme.RealUtility";
        var methodId = "method:Acme.RealUtility.Run()";
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol
            {
                Id = typeId, Name = "RealUtility", Kind = SymbolKind.Type,
                FilePath = "src/RealUtility.cs", Visibility = Visibility.Internal,
                IsStatic = true,
            })
            .AddSymbol(new Symbol
            {
                Id = methodId, Name = "Run", Kind = SymbolKind.Method,
                FilePath = "src/RealUtility.cs", ParentId = typeId,
                Visibility = Visibility.Internal, IsStatic = true,
            })
            .AddEdge(ContainsEdge(typeId, methodId))
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(ExcludePublic: false, ExcludeTests: false));

        Assert.Equal(DeadCodeBucket.Production, findings.Single(f => f.CanonicalId == typeId).Bucket);
        Assert.Equal(DeadCodeBucket.Production, findings.Single(f => f.CanonicalId == methodId).Bucket);
    }

    [Fact]
    public void FindDeadCode_PathExclude_DropsVendoredFindingsKeepsProduction()
    {
        // One genuinely-dead production method + one in a vendored sample tree.
        // pathExclude '*/Examples*/*' must fold the vendored finding out while
        // leaving the production one. INV-DEADCODE-TRIAGE-003.
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Prod", Name = "Prod", Kind = SymbolKind.Type, FilePath = "src/Prod.cs", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol { Id = "method:Acme.Prod.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "src/Prod.cs", ParentId = "type:Acme.Prod", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol { Id = "type:TMPro.Demo", Name = "Demo", Kind = SymbolKind.Type, FilePath = "Assets/TextMesh Pro/Examples & Extras/Demo.cs", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol { Id = "method:TMPro.Demo.B()", Name = "B", Kind = SymbolKind.Method, FilePath = "Assets/TextMesh Pro/Examples & Extras/Demo.cs", ParentId = "type:TMPro.Demo", Visibility = Visibility.Internal })
            .AddEdge(ContainsEdge("type:Acme.Prod", "method:Acme.Prod.A()"))
            .AddEdge(ContainsEdge("type:TMPro.Demo", "method:TMPro.Demo.B()"))
            .Build();

        var findings = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(IncludeKinds: new[] { SymbolKind.Method },
                                ExcludePublic: false, ExcludeTests: false,
                                PathExclude: new[] { "*/Examples*/*" }));

        Assert.Contains(findings, f => f.CanonicalId == "method:Acme.Prod.A()");
        Assert.DoesNotContain(findings, f => f.CanonicalId == "method:TMPro.Demo.B()");
    }

    [Fact]
    public void FindDeadCode_PathExclude_NullOrEmpty_ChangesNothing()
    {
        var graph = new GraphBuilder()
            .AddSymbol(new Symbol { Id = "type:Acme.Prod", Name = "Prod", Kind = SymbolKind.Type, FilePath = "src/Prod.cs", Visibility = Visibility.Internal })
            .AddSymbol(new Symbol { Id = "method:Acme.Prod.A()", Name = "A", Kind = SymbolKind.Method, FilePath = "src/Prod.cs", ParentId = "type:Acme.Prod", Visibility = Visibility.Internal })
            .AddEdge(ContainsEdge("type:Acme.Prod", "method:Acme.Prod.A()"))
            .Build();

        var baseline = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(IncludeKinds: new[] { SymbolKind.Method }, ExcludePublic: false, ExcludeTests: false));
        var withEmpty = new LifebloodDeadCodeAnalyzer().FindDeadCode(graph,
            new DeadCodeOptions(IncludeKinds: new[] { SymbolKind.Method }, ExcludePublic: false, ExcludeTests: false,
                                PathExclude: new[] { "", "   " }));

        Assert.Equal(baseline.Length, withEmpty.Length);
        Assert.Contains(withEmpty, f => f.CanonicalId == "method:Acme.Prod.A()");
    }

    private static Symbol SymInProduction(string id, string name, SymbolKind kind) => new()
    {
        Id = id, Name = name, Kind = kind,
        FilePath = "src/Production/Foo.cs",
        ParentId = id.StartsWith("method:") ? "type:Acme.Prod" : "",
        Visibility = Visibility.Internal,
    };
    private static Symbol SymInTest(string id, string name, SymbolKind kind) => new()
    {
        Id = id, Name = name, Kind = kind,
        FilePath = "src/MySpecTests.cs",
        ParentId = id.StartsWith("method:") ? "type:Acme.Spec" : "",
        Visibility = Visibility.Internal,
    };
    private static Symbol SymInEditor(string id, string name, SymbolKind kind) => new()
    {
        Id = id, Name = name, Kind = kind,
        FilePath = "Assets/Editor/Tools/Tool.cs",
        ParentId = id.StartsWith("method:") ? "type:Acme.Tool" : "",
        Visibility = Visibility.Internal,
    };
    private static Symbol SymInGenerated(string id, string name, SymbolKind kind) => new()
    {
        Id = id, Name = name, Kind = kind,
        FilePath = "Assets/Foo.Generated.cs",
        ParentId = id.StartsWith("method:") ? "type:Acme.Gen" : "",
        Visibility = Visibility.Internal,
    };
    private static Symbol SymInVendored(string id, string name, SymbolKind kind) => new()
    {
        Id = id, Name = name, Kind = kind,
        FilePath = "Assets/TextMesh Pro/Examples & Extras/Demo.cs",
        ParentId = id.StartsWith("method:") ? "type:Acme.Vendor" : "",
        Visibility = Visibility.Internal,
    };
    private static Edge ContainsEdge(string parent, string child) => new()
    {
        SourceId = parent, TargetId = child,
        Kind = EdgeKind.Contains, Evidence = Evidence,
    };
}
