using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-COMPILE-CHECK-FILE-RESOLUTION-001 /
/// INV-COMPILATION-FILE-OWNERSHIP-001 / LB-TRACK-20260530-028.
/// <c>compile_check</c> file-mode must distinguish "pinned module miss",
/// "missing module", "matched no loaded compilation", "ambiguous", and
/// "resolved" as typed states on the
/// result, so a disk-aware caller can separate "path does not exist" from
/// "exists on disk but not in any loaded compilation" (the stale-descriptor
/// case) without parsing the diagnostic message text.
/// </summary>
public class CompileCheckFileResolutionTests
{
    [Fact]
    public void FileMode_UnmatchedPath_ReportsNotInAnyCompilation()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest { FilePath = "Nope.cs" });
        var diagnose = host.GetDiagnosticsReport(new DiagnosticsRequest { FilePath = "Nope.cs" });

        Assert.False(result.Success);
        Assert.Equal(CompileCheckFileResolution.NotInAnyCompilation, result.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.NotFound, result.FileOwnership.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Id == "LB0002");
        Assert.Equal(CompilationFileOwnershipOutcome.NotFound, diagnose.FileOwnership.Outcome);
        Assert.Empty(diagnose.Diagnostics);
    }

    [Fact]
    public void FileMode_PinnedModuleMiss_ReportsNotInModule()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest { FilePath = "Nope.cs", ModuleName = "ModA" });

        Assert.False(result.Success);
        Assert.Equal(CompileCheckFileResolution.NotInModule, result.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.NotInModule, result.FileOwnership.Outcome);
    }

    [Fact]
    public void FileMode_MissingPinnedModule_ReportsModuleNotFound()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = "C.cs",
            ModuleName = "MissingModule",
        });
        var diagnose = host.GetDiagnosticsReport(new DiagnosticsRequest
        {
            FilePath = "C.cs",
            ModuleName = "MissingModule",
        });

        Assert.False(result.Success);
        Assert.Equal(CompileCheckFileResolution.NotInModule, result.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.ModuleNotFound, result.FileOwnership.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("is not loaded", StringComparison.Ordinal));
        Assert.Equal(CompilationFileOwnershipOutcome.ModuleNotFound, diagnose.FileOwnership.Outcome);
        Assert.Empty(diagnose.Diagnostics);
    }

    [Fact]
    public void FileMode_ResolvedFile_ReportsResolved()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = "C.cs",
            Code = "namespace N { public class C { } }",
        });

        Assert.Equal(CompileCheckFileResolution.Resolved, result.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.Unique, result.FileOwnership.Outcome);
        Assert.Equal("ModA", result.FileOwnership.ResolvedModule);
    }

    [Fact]
    public void SnippetMode_ReportsResolved()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest { Code = "var x = 1 + 1;" });

        Assert.Equal(CompileCheckFileResolution.Resolved, result.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.NotRequested, result.FileOwnership.Outcome);
    }

    [Fact]
    public void FileMode_AmbiguousPath_FailsClosedWithStableCandidates()
    {
        using var host = new RoslynCompilationHost(AmbiguousModules());

        var compile = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = "Shared.cs",
            Code = "public class Shared { }",
        });
        var diagnose = host.GetDiagnosticsReport(new DiagnosticsRequest { FilePath = "Shared.cs" });

        Assert.False(compile.Success);
        Assert.Equal(CompileCheckFileResolution.Ambiguous, compile.FileResolution);
        Assert.Equal(CompilationFileOwnershipOutcome.Ambiguous, compile.FileOwnership.Outcome);
        Assert.Equal(new[] { "ModA", "ModB" }, compile.FileOwnership.CandidateModules);
        Assert.Equal(
            new[] { "/workspace/A/Shared.cs", "/workspace/B/Shared.cs" },
            compile.FileOwnership.CandidateFilePaths);
        Assert.Contains(compile.Diagnostics, diagnostic => diagnostic.Id == "LB0004");
        Assert.Equal(CompilationFileOwnershipOutcome.Ambiguous, diagnose.FileOwnership.Outcome);
        Assert.Equal(new[] { "ModA", "ModB" }, diagnose.FileOwnership.CandidateModules);
        Assert.Empty(diagnose.Diagnostics);
        Assert.Empty(diagnose.DefinesActive);
    }

    [Fact]
    public void FileMode_ExplicitModuleOrExactPath_ResolvesOneOwner()
    {
        using var host = new RoslynCompilationHost(AmbiguousModules());

        var pinned = host.CompileCheck(new CompileCheckRequest
        {
            FilePath = "Shared.cs",
            ModuleName = "ModB",
            Code = "public class Shared { }",
        });
        var exact = host.GetDiagnosticsReport(new DiagnosticsRequest
        {
            FilePath = "/workspace/A/Shared.cs",
        });

        Assert.True(pinned.Success);
        Assert.Equal("ModB", pinned.ResolvedModule);
        Assert.Equal(CompilationFileOwnershipOutcome.Unique, pinned.FileOwnership.Outcome);
        Assert.Equal("ModA", exact.ResolvedModule);
        Assert.Equal(CompilationFileOwnershipOutcome.Unique, exact.FileOwnership.Outcome);
    }

    private static Dictionary<string, CSharpCompilation> OneModule()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace N { public class C { } }", path: "C.cs");
        var compilation = CSharpCompilation.Create(
            "ModA",
            new[] { tree },
            BasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal) { ["ModA"] = compilation };
    }

    private static Dictionary<string, CSharpCompilation> AmbiguousModules()
    {
        var references = BasicReferences();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var modB = CSharpCompilation.Create(
            "ModB",
            new[] { CSharpSyntaxTree.ParseText("public class Shared { }", path: "/workspace/B/Shared.cs") },
            references,
            options);
        var modA = CSharpCompilation.Create(
            "ModA",
            new[] { CSharpSyntaxTree.ParseText("public class Shared { }", path: "/workspace/A/Shared.cs") },
            references,
            options);
        return new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["ModB"] = modB,
            ["ModA"] = modA,
        };
    }

    private static MetadataReference[] BasicReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        if (runtimeDir != null)
        {
            foreach (var dll in new[] { "System.Runtime.dll", "netstandard.dll" })
            {
                var path = Path.Combine(runtimeDir, dll);
                if (File.Exists(path))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }
        return refs.ToArray();
    }
}
