using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class PendingToolCallRegistryTests
{
    private static readonly Lazy<Assembly> ProbeAssembly = new(CompileProbeAssembly);

    [Fact]
    public async Task Poll_ActionOnlyIdentityFindsAndConsumesOriginalCall()
    {
        Assert.Equal(
            "Started|Pending|True|Completed|done|Missing",
            await InvokeProbe("ActionOnly"));
    }

    [Fact]
    public async Task Admit_CoalescesExactRetryAndRejectsDifferentArguments()
    {
        Assert.Equal(
            "Started|Coalesced|ConflictingArguments|True|True|1",
            await InvokeProbe("Admissions"));
    }

    [Fact]
    public async Task Registry_TracksDifferentToolsIndependently()
    {
        Assert.Equal("Completed|1|Completed|2", await InvokeProbe("IndependentTools"));
    }

    private static Task<string> InvokeProbe(string methodName)
    {
        var probe = ProbeAssembly.Value.GetType("PendingRegistryProbe", throwOnError: true)!;
        var method = probe.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<Task<string>>(method!.Invoke(null, null));
    }

    private static Assembly CompileProbeAssembly()
    {
        var registryPath = Path.Combine(
            RepoRoot,
            "unity",
            "Editor",
            "LifebloodBridge",
            "PendingToolCallRegistry.cs");
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(File.ReadAllText(registryPath), path: registryPath),
            CSharpSyntaxTree.ParseText(ProbeSource, path: "PendingRegistryProbe.cs"),
        };
        var trustedPlatformAssemblies = Assert.IsType<string>(
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            $"Lifeblood.UnityBridge.PollingProbe.{Guid.NewGuid():N}",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        image.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(image);
    }

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
                current = current.Parent;
            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    private const string ProbeSource = """
        using System;
        using System.Threading.Tasks;
        using Lifeblood.UnityBridge;

        public static class PendingRegistryProbe
        {
            public static async Task<string> ActionOnly()
            {
                var registry = new PendingToolCallRegistry<string>();
                var completion = new TaskCompletionSource<string>();
                var admission = registry.Admit(
                    "lifeblood_analyze",
                    "args-a",
                    () => completion.Task,
                    out var admitted);
                var pending = registry.Poll("lifeblood_analyze", out var pendingTask);
                completion.SetResult("done");
                var completed = registry.Poll("lifeblood_analyze", out var completedTask);
                var value = await completedTask;
                var missing = registry.Poll("lifeblood_analyze", out _);
                return $"{admission}|{pending}|{ReferenceEquals(admitted, pendingTask)}|{completed}|{value}|{missing}";
            }

            public static Task<string> Admissions()
            {
                var registry = new PendingToolCallRegistry<int>();
                var starts = 0;
                var first = registry.Admit(
                    "lifeblood_diagnose",
                    "args-a",
                    () => { starts++; return Task.FromResult(1); },
                    out var firstTask);
                var duplicate = registry.Admit(
                    "lifeblood_diagnose",
                    "args-a",
                    () => { starts++; return Task.FromResult(2); },
                    out var duplicateTask);
                var conflict = registry.Admit(
                    "lifeblood_diagnose",
                    "args-b",
                    () => Task.FromResult(3),
                    out var conflictTask);
                return Task.FromResult(
                    $"{first}|{duplicate}|{conflict}|{ReferenceEquals(firstTask, duplicateTask)}|" +
                    $"{ReferenceEquals(firstTask, conflictTask)}|{starts}");
            }

            public static async Task<string> IndependentTools()
            {
                var registry = new PendingToolCallRegistry<int>();
                registry.Admit("lifeblood_lookup", "A", () => Task.FromResult(1), out _);
                registry.Admit("lifeblood_dependencies", "B", () => Task.FromResult(2), out _);
                var lookupState = registry.Poll("lifeblood_lookup", out var lookup);
                var dependencyState = registry.Poll("lifeblood_dependencies", out var dependencies);
                return $"{lookupState}|{await lookup}|{dependencyState}|{await dependencies}";
            }
        }
        """;
}
