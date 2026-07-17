using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class SourceEvidenceProviderTests
{
    private const string Source = """
        namespace Acme;

        /// <summary>INV-ACME-001 owns this production path.</summary>
        public sealed class Engine
        {
            public void Run()
            {
                // Retired mirror authority; update this comment.
                var receipt = "INV-ACME-001";
            }
        }
        """;

    [Fact]
    public void Scan_ReusesRetainedTreesAndAttributesCommentXmlAndStringEvidence()
    {
        using var host = HostWith(Source);
        var facts = new List<SourceEvidenceFact>();

        var receipt = new ScanSourceEvidenceUseCase((ISourceEvidenceProvider)host).Execute(
            new SourceEvidenceQuery
            {
                SearchTerms = new[] { "INV-ACME-001", "retired mirror" },
                IncludeKinds = SourceEvidenceKind.All,
                MaxFacts = 10,
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Equal(3, facts.Count);
        Assert.Contains(facts, fact =>
            fact.Kind == SourceEvidenceKind.XmlDocumentation
            && fact.MatchedTerm == "INV-ACME-001"
            && fact.ContainingSymbolId == "type:Acme.Engine");
        Assert.Contains(facts, fact =>
            fact.Kind == SourceEvidenceKind.Comment
            && fact.MatchedTerm == "retired mirror"
            && fact.ContainingSymbolId == "method:Acme.Engine.Run()");
        Assert.Contains(facts, fact =>
            fact.Kind == SourceEvidenceKind.StringLiteral
            && fact.MatchedTerm == "INV-ACME-001"
            && fact.ContainingSymbolId == "method:Acme.Engine.Run()");
        Assert.Equal(SourceEvidenceExecutionMode.RetainedSyntaxTrees, receipt.ExecutionMode);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
        Assert.True(receipt.InputIdentityVerifiedAtStart);
        Assert.False(receipt.Truncated);
    }

    [Fact]
    public void Scan_IsBoundedAndRejectsAnotherProfile()
    {
        using var host = HostWith(Source);
        var provider = (ISourceEvidenceProvider)host;
        var facts = new List<SourceEvidenceFact>();
        var receipt = provider.ScanSourceEvidence(
            new SourceEvidenceQuery
            {
                SearchTerms = new[] { "INV-ACME-001" },
                MaxFacts = 1,
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Single(facts);
        Assert.True(receipt.Truncated);
        Assert.Throws<ArgumentException>(() => provider.ScanSourceEvidence(
            new SourceEvidenceQuery
            {
                ProfileScope = "Player",
                SearchTerms = new[] { "INV-ACME-001" },
            },
            _ => true));
    }

    private static RoslynCompilationHost HostWith(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Engine.cs");
        var compilation = CSharpCompilation.Create(
            "Test",
            new[] { tree },
            BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(
            new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
            {
                ["Test"] = compilation,
            },
            retainedProfileName: "Editor",
            availableProfiles: new[] { "Editor", "Player" });
    }

    private static MetadataReference[] BclReferences()
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            foreach (var name in new[] { "System.Runtime.dll", "netstandard.dll" })
            {
                var path = Path.Combine(runtimeDir, name);
                if (File.Exists(path)) references.Add(MetadataReference.CreateFromFile(path));
            }
        }
        return references.ToArray();
    }
}
