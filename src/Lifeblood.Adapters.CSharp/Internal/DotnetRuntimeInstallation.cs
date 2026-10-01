namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>Installation owning the current framework runtime and its SDK host.</summary>
internal static class DotnetRuntimeInstallation
{
    internal static string? RootPath
    {
        get
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            return string.IsNullOrEmpty(runtimeDirectory) ? null
                : Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName;
        }
    }

    internal static string HostPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var candidate = RootPath is { } root
                ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : null;
            return candidate != null && File.Exists(candidate) ? candidate : "dotnet";
        }
    }
}
