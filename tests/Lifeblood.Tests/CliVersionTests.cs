using System.Reflection;
using System.Text.Json;
using Lifeblood.CLI;
using Xunit;

namespace Lifeblood.Tests;

public sealed class CliVersionTests
{
    [Fact]
    public void JsonVersion_IdentifiesCliAssemblyRuntimeAndCompilerWithoutWorkspace()
    {
        using var output = new StringWriter();
        Assert.Equal(0, Program.RunVersion(new[] { "--json" }, output));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("standaloneCli", root.GetProperty("executionMode").GetString());
        Assert.Equal(typeof(Program).Assembly.Location, root.GetProperty("cliAssemblyPath").GetString());
        Assert.Equal(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            root.GetProperty("version").GetString());
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, root.GetProperty("runtime").GetString());
        Assert.Equal(typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            root.GetProperty("roslynVersion").GetString());
        Assert.False(root.TryGetProperty("snapshotId", out _));
    }

    [Fact]
    public void PlainVersion_IsOneLineAndMatchesJsonVersion()
    {
        using var plain = new StringWriter();
        using var json = new StringWriter();
        Assert.Equal(0, Program.RunVersion(Array.Empty<string>(), plain));
        Assert.Equal(0, Program.RunVersion(new[] { "--json" }, json));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal($"Lifeblood {document.RootElement.GetProperty("version").GetString()}{Environment.NewLine}", plain.ToString());
    }
}
