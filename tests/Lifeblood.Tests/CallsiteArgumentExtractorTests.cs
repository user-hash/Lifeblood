using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression tests for INV-CALLSITE-ARGS-001 —
/// <see cref="ICompilationHost.GetCallsiteArguments"/> walks each call site of
/// a target method/constructor and reports supplied-vs-omitted argument facts
/// plus a per-parameter histogram. The dogfood case: an optional parameter
/// exists but every call site omits it. Neutral fixtures (<c>Acme.Api</c>).
/// </summary>
public class CallsiteArgumentExtractorTests
{
    private static MetadataReference[] BclReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference>();
        if (runtimeDir != null)
            foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll", "System.Collections.dll" })
            {
                var path = Path.Combine(runtimeDir, dll);
                if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
            }
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return refs.ToArray();
    }

    private static RoslynCompilationHost HostWith(string source, string moduleName = "Test")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            moduleName, new[] { tree }, BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynCompilationHost(new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            [moduleName] = compilation,
        });
    }

    // Single optional parameter keeps the canonical method id unambiguous.
    private const string ApiType = @"
namespace Acme;
public class Api { public void Do(int lengthSteps = 1) {} }
";

    private const string TargetId = "method:Acme.Api.Do(System.Int32)";
    private static CallsiteArgumentsOptions Default => new();

    [Fact]
    public void CallsiteArguments_OptionalParamOmittedAtEverySite_HistogramReportsOmitted()
    {
        var source = ApiType + @"
public class C1 { public void M() { var a = new Acme.Api(); a.Do(); } }";
        var report = HostWith(source).GetCallsiteArguments(TargetId, Default);

        Assert.NotNull(report);
        Assert.Equal(1, report!.CallSiteCount);
        var summary = Assert.Single(report.ParameterSummaries);
        Assert.Equal("lengthSteps", summary.Name);
        Assert.True(summary.IsOptional);
        Assert.Equal("1", summary.DefaultValueText);
        Assert.Equal(0, summary.SuppliedCount);
        Assert.Equal(1, summary.OmittedCount);

        var arg = Assert.Single(report.Sites[0].Arguments);
        Assert.False(arg.Supplied);
        Assert.Equal("DefaultValue", arg.ArgumentKind);
    }

    [Fact]
    public void CallsiteArguments_MixedSites_HistogramSplitsSuppliedAndOmitted()
    {
        var source = ApiType + @"
public class C1 { public void M() { var a = new Acme.Api(); a.Do(); } }
public class C2 { public void M() { var a = new Acme.Api(); a.Do(7); } }";
        var report = HostWith(source).GetCallsiteArguments(TargetId, Default);

        Assert.NotNull(report);
        Assert.Equal(2, report!.CallSiteCount);
        var summary = Assert.Single(report.ParameterSummaries);
        Assert.Equal(1, summary.SuppliedCount);
        Assert.Equal(1, summary.OmittedCount);
    }

    [Fact]
    public void CallsiteArguments_SuppliedLiteral_ClassifiesValueKindAndRawText()
    {
        var source = ApiType + @"
public class C2 { public void M() { var a = new Acme.Api(); a.Do(7); } }";
        var report = HostWith(source).GetCallsiteArguments(TargetId, Default);

        var arg = Assert.Single(report!.Sites[0].Arguments);
        Assert.True(arg.Supplied);
        Assert.Equal("Explicit", arg.ArgumentKind);
        Assert.Equal(CallsiteArgumentValueKind.Literal, arg.ValueKind);
        Assert.Equal("7", arg.RawText);
        Assert.True(arg.IsConstant);
    }

    [Fact]
    public void CallsiteArguments_SiteCarriesContainingSymbolAndReceiver()
    {
        var source = ApiType + @"
public class C2 { public void M() { var a = new Acme.Api(); a.Do(7); } }";
        var report = HostWith(source).GetCallsiteArguments(TargetId, Default);

        var site = Assert.Single(report!.Sites);
        Assert.Equal("a", site.Receiver);
        Assert.Equal("Test", site.ModuleName);
        Assert.NotNull(site.ContainingSymbolId);
        Assert.Contains("C2.M", site.ContainingSymbolId!);
    }

    [Fact]
    public void CallsiteArguments_NonMethodTarget_ReturnsNull()
    {
        var report = HostWith(ApiType).GetCallsiteArguments("type:Acme.Api", Default);
        Assert.Null(report);
    }
}
