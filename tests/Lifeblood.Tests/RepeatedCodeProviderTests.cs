using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Lifeblood.Tests;

public sealed class RepeatedCodeProviderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lifeblood-duplicate-fixture");
    private const string Source = """
        public class Engine {
            public int A(int value) { var result = value + 1; return result * 2; }
            public int B(int value) { /* formatting */ var result=value+1; return result*2; }
            public int C(int renamed) { var result = renamed + 1; return result * 2; }
            public int D(int value) { var result = value + 3; return result * 2; }
        }
        """;

    [Fact]
    public void ExactBodies_IgnoreTriviaButPreserveNamesAndLiteralsAndState()
    {
        var compilation = Compilation(("Engine.cs", Source));
        var originalTrees = compilation.SyntaxTrees.ToArray();
        var provider = Provider(compilation);
        var query = new RepeatedCodeQuery { MinTokens = 1 };
        var report = provider.Find(query, default);
        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.OccurrenceCount);
        Assert.Equal(new[] { "method:Engine.A(int)", "method:Engine.B(int)" }, group.Members.Select(member => member.ContainingSymbolId));
        Assert.All(group.Members, member => Assert.Equal("Engine.cs", member.Source.FilePath));
        Assert.True(group.TokenCount > 10);
        Assert.False(report.Truncated);
        Assert.Equal(4, report.ComparedBodies);
        Assert.Equal(0, report.AdditionalSemanticBaseCount);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(provider.Find(query, default)));
        Assert.Equal(originalTrees, compilation.SyntaxTrees.ToArray());
    }

    [Fact]
    public void SameTokensWithDifferentBindings_RemainAdvisory()
    {
        var compilation = Compilation(("Bindings.cs", """
            public class A { private int Read(int value) => value + 1; public int Run(int value) { return Read(value); } }
            public class B { private int Read(int value) => value - 1; public int Run(int value) { return Read(value); } }
            """));
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var targets = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(node => model.GetSymbolInfo(node).Symbol!.ContainingType.Name).ToArray();
        Assert.Equal(new[] { "A", "B" }, targets);
        var report = Provider(compilation).Find(new RepeatedCodeQuery { MinTokens = 1 }, default);
        Assert.Equal(2, Assert.Single(report.Groups).OccurrenceCount);
        Assert.Contains(report.Limitations, limitation => limitation.Contains("do not establish equal bindings"));
    }

    [Fact]
    public void NestedLocalFunctionsAndLambdas_AreComparedOnlyInsideOuterBodies()
    {
        var report = Provider(Compilation(("Nested.cs", """
            using System;
            public class Engine {
                public int A(int value) { int Local(int other) => other + 1; Func<int,int> f = x => x * 2; return f(Local(value)); }
                public int B(int value) { int Local(int other) => other + 1; Func<int,int> f = x => x * 2; return f(Local(value)); }
            }
            """))).Find(new RepeatedCodeQuery { MinTokens = 1 }, default);
        Assert.Equal(2, report.ObservedBodies);
        Assert.Equal(2, Assert.Single(report.Groups).OccurrenceCount);
    }

    [Fact]
    public void SourceScopeAndDefaultExclusions_UseWorkspacePaths()
    {
        var compilation = Compilation(
            ("A.cs", "public class A { public int Run(int value) => value + 1; }"),
            ("Tests/BTests.cs", "public class B { public int Run(int value) => value + 1; }"),
            ("C.g.cs", "public class C { public int Run(int value) => value + 1; }"));
        var provider = Provider(compilation);
        var defaults = provider.Find(new RepeatedCodeQuery { MinTokens = 1 }, default);
        Assert.Empty(defaults.Groups);
        Assert.Equal(2, defaults.ExcludedFiles);
        var all = provider.Find(new RepeatedCodeQuery { MinTokens = 1, ExcludeGenerated = false, ExcludeTests = false }, default);
        Assert.Equal(3, Assert.Single(all.Groups).OccurrenceCount);
        var scoped = provider.Find(new RepeatedCodeQuery { MinTokens = 1, FilePaths = new[] { "A.cs" } }, default);
        Assert.Equal(1, scoped.ScannedFiles);
        Assert.Empty(scoped.Groups);
        Assert.Empty(provider.Find(new RepeatedCodeQuery { ModuleScope = "Other" }, default).Groups);
        Assert.Throws<ArgumentException>(() => provider.Find(new RepeatedCodeQuery { ProfileScope = "Player" }, default));
    }

    [Theory]
    [InlineData("MaxBodies")]
    [InlineData("MaxTokens")]
    [InlineData("MaxNodes")]
    [InlineData("MaxSourceCharacters")]
    [InlineData("MaxFiles")]
    public void InputBounds_StopWithAnExplicitPartialReceipt(string limit)
    {
        var query = limit switch {
            "MaxBodies" => new RepeatedCodeQuery { MinTokens = 1, MaxBodies = 1 },
            "MaxTokens" => new RepeatedCodeQuery { MinTokens = 1, MaxTokens = 1 },
            "MaxNodes" => new RepeatedCodeQuery { MinTokens = 1, MaxNodes = 1 },
            "MaxSourceCharacters" => new RepeatedCodeQuery { MinTokens = 1, MaxSourceCharacters = 1 },
            _ => new RepeatedCodeQuery { MinTokens = 1, MaxFiles = 1 },
        };
        var report = Provider(Compilation(("A.cs", Source), ("B.cs", "public class Other { }"))).Find(query, default);
        Assert.True(report.Truncated);
        Assert.Contains(limit, report.ReachedLimits);
        Assert.True(report.ObservedBodies <= query.MaxBodies);
        Assert.True(report.ScannedTokens <= query.MaxTokens);
        Assert.True(report.ScannedNodes <= query.MaxNodes);
        Assert.True(report.SourceCharacters <= query.MaxSourceCharacters);
        Assert.True(report.ScannedFiles <= query.MaxFiles);
    }

    [Fact]
    public void OutputBounds_PreserveObservedCountsAndCancellationAndValidateHardLimits()
    {
        var provider = Provider(Compilation(("Many.cs", """
            public class Engine {
                public int A(int value) => value + 1;
                public int B(int value) => value + 1;
                public int C(int value) => value + 1;
                public int D(int value) => value + 2;
                public int E(int value) => value + 2;
            }
            """)));
        var report = provider.Find(new RepeatedCodeQuery { MinTokens = 1, MaxGroups = 1, MaxMembersPerGroup = 2 }, default);
        Assert.Equal(2, report.GroupCount);
        var group = Assert.Single(report.Groups);
        Assert.Equal(3, group.OccurrenceCount);
        Assert.Equal(2, group.Members.Length);
        Assert.True(group.MembersTruncated);
        Assert.Contains("MaxGroups", report.ReachedLimits);
        Assert.Contains("MaxMembersPerGroup", report.ReachedLimits);
        Assert.Throws<OperationCanceledException>(() => provider.Find(new RepeatedCodeQuery(), new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Find(new RepeatedCodeQuery { MaxTokens = int.MaxValue }, default));
    }

    [Theory]
    [InlineData(CanonicalSymbolIdVersion.LegacyV1)]
    [InlineData(CanonicalSymbolIdVersion.AssemblyQualifiedV2)]
    public void SupportedBodies_UseCanonicalDeclarationOwners(CanonicalSymbolIdVersion version)
    {
        var compilation = Compilation(("Kinds.cs", """
            public class A {
                public A() { }
                ~A() { }
                public int M(int value) => value + 1;
                public int P => 1;
                public int Q { get => 1; }
                public int this[int value] => value + 1;
                public static int operator +(A item, int value) => value + 1;
            }
            """));
        var provider = new RoslynRepeatedCodeProvider(new Dictionary<string, CSharpCompilation> { ["Test"] = compilation },
            WorkspaceSourcePathMap.Create(Root), "Editor", new[] { "Editor" }, version);
        var report = provider.Find(new RepeatedCodeQuery { MinTokens = 1 }, default);
        Assert.Equal(7, report.ComparedBodies);
        Assert.Equal(3, report.Groups.Length);
        var members = report.Groups.SelectMany(group => group.Members).ToArray();
        Assert.Equal(new[] { "Accessor", "Constructor", "Destructor", "Method", "Operator", "Property", "Property" },
            members.Select(member => member.BodyKind).Order(StringComparer.Ordinal));
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        var owners = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
            .Where(node => node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax)
            .Select(node => model.GetDeclaredSymbol(node)!)
            .Select(symbol => CanonicalSymbolFormat.BuildSymbolId(symbol.OriginalDefinition, version)).ToHashSet();
        Assert.All(members, member => Assert.Contains(member.ContainingSymbolId, owners));
    }

    [Fact]
    public void ActiveProfile_ExcludesDisabledText()
    {
        const string source = """
            public class Engine {
                public int A(int value) => value + 1;
                #if UNITY_EDITOR
                public int B(int value) => value + 1;
                #else
                public int B(int value) => value + 2;
                #endif
            }
            """;
        var compilation = Compilation(("Profile.cs", source));
        CSharpCompilation ForProfile(params string[] defines) => compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithPreprocessorSymbols(defines), Path.Combine(Root, "Profile.cs")));
        Assert.Single(Provider(ForProfile("UNITY_EDITOR")).Find(new RepeatedCodeQuery { MinTokens = 1 }, default).Groups);
        Assert.Empty(Provider(ForProfile()).Find(new RepeatedCodeQuery { MinTokens = 1 }, default).Groups);
    }

    [Fact]
    public void LinkedFile_IsOnePhysicalOccurrencePerBody()
    {
        var compilation = Compilation(("Linked.cs", Source));
        var provider = new RoslynRepeatedCodeProvider(new Dictionary<string, CSharpCompilation> {
            ["First"] = compilation, ["Second"] = compilation.WithAssemblyName("Second") },
            WorkspaceSourcePathMap.Create(Root), "Editor", new[] { "Editor" }, CanonicalSymbolIdVersion.LegacyV1);
        var report = provider.Find(new RepeatedCodeQuery { MinTokens = 1 }, default);
        Assert.Equal(4, report.ObservedBodies);
        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.OccurrenceCount);
        Assert.All(group.Members, member => Assert.Equal("First", member.ModuleName));
        var scoped = provider.Find(new RepeatedCodeQuery { MinTokens = 1, ModuleScope = "Second" }, default);
        Assert.All(Assert.Single(scoped.Groups).Members, member => Assert.Equal("Second", member.ModuleName));
    }

    [Fact]
    public void LiteralSpellingAndMinimumSize_AreExactPolicy()
    {
        var compilation = Compilation(("Spellings.cs", """
            public class Engine {
                public int A() => 1;
                public int B() => 0x1;
                public string C() => "a";
                public string D() => "\u0061";
                public int E() => 1;
            }
            """));
        var provider = Provider(compilation);
        var group = Assert.Single(provider.Find(new RepeatedCodeQuery { MinTokens = 1 }, default).Groups);
        Assert.Equal(new[] { "method:Engine.A()", "method:Engine.E()" }, group.Members.Select(member => member.ContainingSymbolId));
        Assert.Empty(provider.Find(new RepeatedCodeQuery { MinTokens = 2 }, default).Groups);
    }

    private static CSharpCompilation Compilation(params (string Path, string Source)[] files)
    {
        var compilation = CSharpCompilation.Create("Test", files.Select(file =>
            CSharpSyntaxTree.ParseText(file.Source, CSharpParseOptions.Default, Path.Combine(Root, file.Path))),
            BclReferenceLoader.References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        return compilation;
    }

    private static RoslynRepeatedCodeProvider Provider(CSharpCompilation compilation) => new(
        new Dictionary<string, CSharpCompilation> { ["Test"] = compilation },
        WorkspaceSourcePathMap.Create(Root), "Editor", new[] { "Editor", "Player" }, CanonicalSymbolIdVersion.LegacyV1);
}
