using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lifeblood.CLI;

internal sealed record CliIdentity(
    int SchemaVersion, string ExecutionMode, string Version, string CliAssemblyPath,
    string? ProcessPath, string Runtime, string? TargetFramework, string? RoslynVersion)
{
    internal static CliIdentity Capture()
    {
        var assembly = typeof(Program).Assembly;
        return new(1, "standaloneCli",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unknown",
            assembly.Location, Environment.ProcessPath, RuntimeInformation.FrameworkDescription,
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
}
