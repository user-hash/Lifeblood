using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-COMPILE-CHECK-FILE-RESOLUTION-001 / LB-TRACK-20260530-028.
/// <c>compile_check</c> file-mode must distinguish "pinned module miss",
/// "matched no loaded compilation", and "resolved" as typed states on the
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

        Assert.False(result.Success);
        Assert.Equal(CompileCheckFileResolution.NotInAnyCompilation, result.FileResolution);
        Assert.Contains(result.Diagnostics, d => d.Id == "LB0002");
    }

    [Fact]
    public void FileMode_PinnedModuleMiss_ReportsNotInModule()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest { FilePath = "Nope.cs", ModuleName = "ModA" });

        Assert.False(result.Success);
        Assert.Equal(CompileCheckFileResolution.NotInModule, result.FileResolution);
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
    }

    [Fact]
    public void SnippetMode_ReportsResolved()
    {
        using var host = new RoslynCompilationHost(OneModule());

        var result = host.CompileCheck(new CompileCheckRequest { Code = "var x = 1 + 1;" });

        Assert.Equal(CompileCheckFileResolution.Resolved, result.FileResolution);
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
