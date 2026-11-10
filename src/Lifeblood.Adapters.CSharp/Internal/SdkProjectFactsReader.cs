using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Lifeblood.Adapters.CSharp.Internal;

internal sealed record SdkProjectFacts(
    string LanguageVersion,
    string DeclaredLanguageVersion,
    string TargetFramework,
    string SdkVersion,
    string MsbuildVersion,
    string CompilerVersion,
    string[] Imports);

/// <summary>
/// Evaluates one project batch in the workspace-selected SDK process. Only
/// neutral property/import receipts cross back; SDK assemblies and evaluated
/// projects are never loaded or retained in the semantic host.
/// </summary>
internal static class SdkProjectFactsReader
{
    internal static IReadOnlyDictionary<string, SdkProjectFacts> Read(
        string projectRoot, IReadOnlyList<string> projectFiles)
    {
        var result = new Dictionary<string, SdkProjectFacts>(StringComparer.OrdinalIgnoreCase);
        if (projectFiles.Count == 0) return result;

        using var resource = typeof(SdkProjectFactsReader).Assembly.GetManifestResourceStream(
            "Lifeblood.Adapters.CSharp.Internal.SdkProjectFacts.proj")
            ?? throw new InvalidOperationException("SDK project evaluation resource is unavailable.");
        var project = XDocument.Load(resource);
        var items = project.Root!.Element("ItemGroup")!;
        for (var i = 0; i < projectFiles.Count; i++)
        {
            items.Add(new XElement("Input", new XAttribute("Include", i),
                new XElement("PathBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(projectFiles[i])))));
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"lifeblood-sdk-facts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var projectPath = Path.Combine(temporaryDirectory, "evaluate.proj");
        try
        {
            project.Save(projectPath);
            var start = new ProcessStartInfo(DotnetRuntimeInstallation.HostPath)
            {
                WorkingDirectory = Path.GetFullPath(projectRoot),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("msbuild");
            start.ArgumentList.Add(projectPath);
            start.ArgumentList.Add("-nologo");
            start.ArgumentList.Add("-verbosity:quiet");
            start.ArgumentList.Add("-getTargetResult:EvaluateProjects");
            var text = RunEvaluation(start, 30_000);

            using var json = JsonDocument.Parse(text);
            var facts = json.RootElement.GetProperty("TargetResults")
                .GetProperty("EvaluateProjects").GetProperty("Items");
            var imports = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in facts.EnumerateArray())
            {
                if (item.GetProperty("FactKind").GetString() != "Import") continue;
                var owner = item.GetProperty("ProjectFile").GetString()!;
                if (!imports.TryGetValue(owner, out var paths)) imports[owner] = paths = new();
                paths.Add(item.GetProperty("Identity").GetString()!);
            }
            foreach (var item in facts.EnumerateArray())
            {
                if (item.GetProperty("FactKind").GetString() != "Project") continue;
                var path = item.GetProperty("Identity").GetString()!;
                var language = item.GetProperty("LangVersion").GetString()!;
                if (string.IsNullOrWhiteSpace(language))
                    throw new InvalidOperationException($"SDK did not resolve LangVersion for '{path}'.");
                result.Add(path, new SdkProjectFacts(language,
                    item.GetProperty("DeclaredLangVersion").GetString()!,
                    item.GetProperty("TargetFramework").GetString()!,
                    item.GetProperty("NETCoreSdkVersion").GetString()!,
                    item.GetProperty("MSBuildVersion").GetString()!,
                    item.GetProperty("CompilerVersion").GetString()!,
                    imports.GetValueOrDefault(path)?.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>()));
            }
            if (result.Count != projectFiles.Count || projectFiles.Any(path => !result.ContainsKey(path)))
                throw new InvalidOperationException("SDK evaluation did not return the complete requested project batch.");
            return result;
        }
        finally
        {
            File.Delete(projectPath);
            Directory.Delete(temporaryDirectory);
        }
    }

    internal static string RunEvaluation(ProcessStartInfo start, int timeoutMs)
    {
        using var nullInput = File.OpenNullHandle();
        start.StandardInputHandle = nullInput;
        if (OperatingSystem.IsWindows()) start.InheritedHandles = [];
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to start SDK project evaluation.");
        var clock = Stopwatch.StartNew();
        try
        {
            var (output, error) = process.ReadAllText(TimeSpan.FromMilliseconds(timeoutMs));
            var remainingMs = checked((int)Math.Max(0, timeoutMs - clock.ElapsedMilliseconds));
            if (!process.WaitForExit(remainingMs)) throw new TimeoutException();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"SDK project evaluation failed: {error}");
            return output;
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"SDK project evaluation exceeded {timeoutMs / 1000.0:g} seconds.", ex);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(1000))
                    throw new InvalidOperationException("SDK project evaluation did not exit after termination.");
            }
        }
    }
}
