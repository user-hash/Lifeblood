using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.Performance;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Analysis;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Lifeblood.Tests;

public sealed class NativeSourceEvidenceTests
{
    [Theory]
    [InlineData(SourceHashAlgorithm.Sha1)]
    [InlineData(SourceHashAlgorithm.Sha256)]
    public void ActualPortablePdbDocumentMatchesTheSameRetainedCompilerTextWithoutAuthenticatingTheArtifact(SourceHashAlgorithm algorithm)
    {
        var text = SourceText.From("public class NativeFixture { public int Run() => 7; }", Encoding.UTF8, algorithm);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp9), "Native.cs");
        var compilation = CSharpCompilation.Create("NativeFixture", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        pdb.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb, MetadataStreamOptions.LeaveOpen);
        var reader = provider.GetMetadataReader();
        var document = reader.GetDocument(Assert.Single(reader.Documents));
        var checksum = Convert.ToHexString(reader.GetBlobBytes(document.Hash)).ToLowerInvariant();
        Assert.Equal(algorithm == SourceHashAlgorithm.Sha1 ? new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") : new Guid("8829d00f-11b8-4213-878b-770e8597ac16"), reader.GetGuid(document.HashAlgorithm));
        var debugId = Convert.ToHexString(reader.DebugMetadataHeader!.Id.AsSpan());
        pe.Position = 0;
        using var peReader = new PEReader(pe, PEStreamOptions.LeaveOpen);
        var codeView = peReader.ReadCodeViewDebugDirectoryData(Assert.Single(peReader.ReadDebugDirectory(), entry => entry.Type == DebugDirectoryEntryType.CodeView));
        Assert.Equal(codeView.Guid, new Guid(reader.DebugMetadataHeader.Id.AsSpan(0, 16)));
        var build = Convert.ToHexString(SHA256.HashData(pe.ToArray()));
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation> { ["NativeFixture"] = compilation }, retainedProfileName: "Editor");
        var facts = new List<OperationFact>();
        var receipt = ((IOperationFactProvider)host).ScanOperationFacts(new OperationFactQuery
        {
            IncludeCallableDeclarations = true, IncludeKinds = new[] { OperationFactKind.CallableDeclaration },
        }, fact => { facts.Add(fact); return true; });
        var declaration = Assert.Single(facts).CallableDeclaration!;
        Assert.Equal(checksum, declaration.SourceChecksums[algorithm.ToString()]);
        Assert.Equal(2, declaration.SourceChecksums.Count);
        var observations = declaration.SourceChecksums.Select(pair => new NativeSourceObservation("Native.cs", "NativeFixture", "Editor", pair.Key, pair.Value)).ToArray();
        var capture = Capture(checksum, algorithm.ToString(), build: build, artifactBuild: build, debugId: debugId);
        var result = NativeSourceJoinAnalyzer.Compare(capture, observations, receipt, ignorePathCase: false);
        Assert.Equal(NativeSourceJoinOutcome.MatchedSourceChecksum, Assert.Single(result.Documents).Outcome);
        Assert.False(result.NativeArtifactVerified);
        Assert.Equal("CallerReported", result.EvidenceOrigin);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
        Assert.Equal(0, receipt.CompiledModuleCount);
    }

    [Theory]
    [InlineData("missing", NativeSourceJoinOutcome.MissingIdentity)]
    [InlineData("build", NativeSourceJoinOutcome.BuildMismatch)]
    [InlineData("profile", NativeSourceJoinOutcome.ProfileMismatch)]
    [InlineData("domain", NativeSourceJoinOutcome.ChecksumDomainMismatch)]
    [InlineData("algorithm", NativeSourceJoinOutcome.UnsupportedChecksumAlgorithm)]
    [InlineData("invalid", NativeSourceJoinOutcome.InvalidChecksum)]
    [InlineData("mismatch", NativeSourceJoinOutcome.MismatchedSourceChecksum)]
    [InlineData("unobserved", NativeSourceJoinOutcome.SourceNotObserved)]
    [InlineData("ambiguous", NativeSourceJoinOutcome.AmbiguousSourceIdentity)]
    [InlineData("truncated", NativeSourceJoinOutcome.IncompleteSemanticScan)]
    public void UnqualifiedIdentitiesNeverBecomeMatches(string scenario, string expected)
    {
        var hash = new string('a', 64);
        var capture = Capture(scenario == "invalid" ? "not-hex" : scenario == "mismatch" ? new string('b', 64) : hash,
            scenario == "algorithm" ? "MD5" : "SHA-256", artifactBuild: scenario == "build" ? "other" : "build",
            profile: scenario == "profile" ? "Player" : "Editor", debugId: scenario == "missing" ? "" : "debug",
            checksumKind: scenario == "domain" ? "RawFile" : "DebugDocument");
        var observations = new List<NativeSourceObservation>();
        if (scenario != "unobserved") observations.Add(new("Native.cs", "NativeFixture", "Editor", "Sha256", hash));
        if (scenario == "ambiguous") observations.Add(new("Native.cs", "NativeFixture", "Editor", "Sha256", new string('c', 64)));
        var result = NativeSourceJoinAnalyzer.Compare(capture, observations, Receipt(scenario == "truncated"), false);
        Assert.Equal(expected, Assert.Single(result.Documents).Outcome);
        Assert.False(result.NativeArtifactVerified);
    }

    [Theory]
    [InlineData("{\"artifactId\":\"a\",\"artifactId\":\"b\"}")]
    [InlineData("{\"artifactId\":42}")]
    [InlineData("{\"unknown\":\"field\"}")]
    [InlineData("{\"documents\":{}}")]
    public void NativeIdentityImportRejectsMalformedOrAmbiguousObjects(string artifact)
    {
        var json = "{\"measurements\":[],\"nativeArtifacts\":[" + artifact + "]}";
        Assert.Throws<ArgumentException>(() => new RuntimePerformanceEvidenceImporter().Import(new("capture.json", json, "json", 10)));
    }

    [Fact]
    public void ImportRejectsArtifactAndDocumentOverflowInsteadOfDroppingIdentityRows()
    {
        var tooMany = JsonSerializer.Serialize(new { measurements = Array.Empty<object>(), nativeArtifacts = Enumerable.Range(0, 33).Select(i => new { artifactId = i.ToString() }) });
        Assert.Throws<ArgumentException>(() => new RuntimePerformanceEvidenceImporter().Import(new("capture.json", tooMany, null, 10)));
        var documents = JsonSerializer.Serialize(new { measurements = Array.Empty<object>(), nativeArtifacts = new[] { new { artifactId = "a", documents = Enumerable.Range(0, 257).Select(_ => new { }) } } });
        Assert.Throws<ArgumentException>(() => new RuntimePerformanceEvidenceImporter().Import(new("capture.json", documents, null, 10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PdbSha256CanJoinDefaultRetainedTextButLostBomNeverBecomesAnAssumedMatch(bool bom)
    {
        const string source = "public class NativeFixture { public int Run() => 7; }";
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        using var input = new MemoryStream(bytes);
        var producerText = SourceText.From(input, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var options = new CSharpParseOptions(LanguageVersion.CSharp9);
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        CSharpCompilation Compile(SyntaxTree tree) => CSharpCompilation.Create("NativeFixture", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var producer = Compile(CSharpSyntaxTree.ParseText(producerText, options, "Native.cs"));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        Assert.True(producer.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success);
        pdb.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb, MetadataStreamOptions.LeaveOpen);
        var reader = provider.GetMetadataReader();
        var nativeHash = Convert.ToHexString(reader.GetBlobBytes(reader.GetDocument(Assert.Single(reader.Documents)).Hash)).ToLowerInvariant();
        using var host = new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>
        {
            ["NativeFixture"] = Compile(CSharpSyntaxTree.ParseText(source, options, "Native.cs")),
        }, retainedProfileName: "Editor");
        var facts = new List<OperationFact>();
        var receipt = ((IOperationFactProvider)host).ScanOperationFacts(new OperationFactQuery
        { IncludeCallableDeclarations = true, IncludeKinds = new[] { OperationFactKind.CallableDeclaration } }, fact => { facts.Add(fact); return true; });
        var declaration = Assert.Single(facts).CallableDeclaration!;
        Assert.Equal("Sha1", declaration.SourceChecksumAlgorithm);
        Assert.Null(declaration.SourceEncoding);
        var observations = declaration.SourceChecksums.Select(pair => new NativeSourceObservation("Native.cs", "NativeFixture", "Editor", pair.Key, pair.Value)).ToArray();
        var result = NativeSourceJoinAnalyzer.Compare(Capture(nativeHash, "Sha256"), observations, receipt, false);
        Assert.Equal(bom ? NativeSourceJoinOutcome.MismatchedSourceChecksum : NativeSourceJoinOutcome.MatchedSourceChecksum, Assert.Single(result.Documents).Outcome);
    }

    [Theory]
    [InlineData("{\"identity\":{\"buildId\":\"a\",\"buildId\":\"b\"},\"measurements\":[],\"nativeArtifacts\":[]}")]
    [InlineData("{\"measurements\":[],\"nativeArtifacts\":[],\"nativeArtifacts\":[]}")]
    public void DuplicateCaptureIdentityFieldsAreRejectedWhenNativeJoinsArePresent(string json)
        => Assert.Throws<ArgumentException>(() => new RuntimePerformanceEvidenceImporter().Import(new("capture.json", json, null, 10)));

    [Fact]
    public void NativeComparisonBoundsCaptureIdentityEvenWhenTheGenericImporterAcceptsIt()
    {
        var json = JsonSerializer.Serialize(new { captureId = new string('x', 257), identity = new { buildId = "build" },
            measurements = Array.Empty<object>(), nativeArtifacts = Array.Empty<object>() });
        var capture = new RuntimePerformanceEvidenceImporter().Import(new("capture.json", json, null, 10));
        Assert.Throws<ArgumentException>(() => NativeSourceJoinAnalyzer.Compare(capture, Array.Empty<NativeSourceObservation>(), Receipt(false), false));
    }

    private static PerformanceCapture Capture(string checksum, string algorithm, string build = "build", string artifactBuild = "build",
        string profile = "Editor", string debugId = "debug", string checksumKind = "DebugDocument")
    {
        var json = JsonSerializer.Serialize(new
        {
            identity = new { buildId = build, defineProfiles = new[] { "Editor", "Player" } }, measurements = Array.Empty<object>(),
            nativeArtifacts = new[] { new { artifactId = "NativeFixture.dll", buildId = artifactBuild, debugId, debugFormat = "PortablePdb",
                documents = new[] { new { workspaceFilePath = "Native.cs", sourcePath = "Native.cs", moduleName = "NativeFixture", profileScope = profile, checksumKind, checksumAlgorithm = algorithm, checksum } } } },
        });
        return new RuntimePerformanceEvidenceImporter().Import(new("capture.json", json, null, 10));
    }

    private static OperationFactScanReceipt Receipt(bool truncated) => new()
    {
        Status = OperationFactScanStatus.Completed, ProfileScope = "Editor", ExecutionMode = OperationFactExecutionMode.RetainedCompilation,
        InputIdentityVerifiedAtStart = true, AdditionalSemanticBaseCount = 0, CompiledModuleCount = 0, ScannedModuleCount = 1, ScannedFileCount = 1,
        ObservedOperationCount = 1, EmittedFactCount = 1, Truncated = truncated, StoppedByConsumer = false,
    };
}
