using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-OPERATION-FACTS-001. Non-primary define profiles execute from the
/// committed analysis identity without becoming another retained Roslyn base.
/// </summary>
public sealed class ProfileScopedOperationFactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lifeblood-operation-profile-{Guid.NewGuid():N}");

    public ProfileScopedOperationFactTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        var packageRoot = Path.Combine(_root, "packages");
        var packageDllDirectory = Path.Combine(packageRoot, "demo", "1.0.0", "lib", "net8.0");
        Directory.CreateDirectory(packageDllDirectory);
        File.Copy(
            typeof(FactAttribute).Assembly.Location,
            Path.Combine(packageDllDirectory, "Demo.dll"));
        var packageRootJson = packageRoot.Replace('\\', '/') + "/";
        File.WriteAllText(
            Path.Combine(_root, "obj", "project.assets.json"),
            """{"version":3,"targets":{"net8.0":{"demo/1.0.0":{"compile":{"lib/net8.0/Demo.dll":{}}}}},"packageFolders":{"__PACKAGE_ROOT__":{}}}"""
                .Replace("__PACKAGE_ROOT__", packageRootJson, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Profiled.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>Profiled</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Profiled.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Profiled.cs"), """
            namespace Acme;
            public sealed class Profiled
            {
                private static void Sink(int value) { }
                public void Run()
                {
            #if PLAYER_ONLY
                    Sink(42);
                    Sink(43);
                    var mask = 1UL << 42;
            #else
                    Sink(7);
            #endif
                }
            }
            """);
    }

    [Fact]
    public void Scan_SecondaryProfileCompilesEphemerallyWithoutReplacingRetainedBase()
    {
        var analyzer = Analyze();
        var retained = Assert.Single(analyzer.Compilations!);
        var facts = new List<OperationFact>();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery
            {
                ProfileScope = "Player",
                IncludeKinds = new[] { OperationFactKind.Call },
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Equal(OperationFactScanStatus.Completed, receipt.Status);
        Assert.Equal(OperationFactExecutionMode.EphemeralProfileCompilation, receipt.ExecutionMode);
        Assert.True(receipt.InputIdentityVerifiedAtStart);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
        Assert.Equal(1, receipt.CompiledModuleCount);
        Assert.Equal(new[] { "42", "43" }, facts.Select(ArgumentConstant));
        Assert.Same(retained.Value, Assert.Single(analyzer.Compilations!).Value);
    }

    [Fact]
    public void Scan_RetainedProfileUsesImmutableCompilationWithoutRecompile()
    {
        var analyzer = Analyze();
        var facts = new List<OperationFact>();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery
            {
                ProfileScope = "Editor",
                IncludeKinds = new[] { OperationFactKind.Call },
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Equal(OperationFactExecutionMode.RetainedCompilation, receipt.ExecutionMode);
        Assert.Equal(0, receipt.CompiledModuleCount);
        Assert.Equal("7", ArgumentConstant(Assert.Single(facts)));
    }

    [Fact]
    public void Scan_SecondaryProfilePreservesTargetlessDisjunctiveSelectors()
    {
        var analyzer = Analyze();
        var facts = new List<OperationFact>();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery
            {
                ProfileScope = "Player",
                Selectors = new[]
                {
                    new OperationFactSelector
                    {
                        IncludeKinds = new[] { OperationFactKind.Binary },
                        ContainingSymbolIds = new[] { "method:Acme.Profiled.Run()" },
                        Operators = new[] { "LeftShift" },
                    },
                },
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        var shift = Assert.Single(facts);
        Assert.Equal("LeftShift", shift.Operator);
        Assert.Equal("ulong", shift.ResultType);
        Assert.Equal(OperationFactExecutionMode.EphemeralProfileCompilation, receipt.ExecutionMode);
        Assert.Equal(0, receipt.AdditionalSemanticBaseCount);
    }

    [Fact]
    public void Scan_SecondaryProfileRejectsSourceDriftBeforeEmittingFacts()
    {
        var analyzer = Analyze();
        File.AppendAllText(Path.Combine(_root, "Profiled.cs"), Environment.NewLine + "// drift");
        var facts = new List<OperationFact>();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery { ProfileScope = "Player" },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Equal(OperationFactScanStatus.Rejected, receipt.Status);
        Assert.Equal(OperationFactRejectionReason.InputDrift, receipt.RejectionReason);
        Assert.False(receipt.InputIdentityVerifiedAtStart);
        Assert.Equal(0, receipt.CompiledModuleCount);
        Assert.Empty(facts);
    }

    [Fact]
    public void Scan_SecondaryProfileRejectsNuGetAssetsDriftBeforeEmittingFacts()
    {
        var analyzer = Analyze();
        File.WriteAllText(
            Path.Combine(_root, "obj", "project.assets.json"),
            """{"version":3,"targets":{"changed":{}},"packageFolders":{}}""");

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery { ProfileScope = "Player" },
            _ => true);

        Assert.Equal(OperationFactScanStatus.Rejected, receipt.Status);
        Assert.Equal(OperationFactRejectionReason.InputDrift, receipt.RejectionReason);
        Assert.Contains(receipt.Limitations, limitation =>
            limitation.Contains("reference-set:Profiled", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SecondaryProfileHonorsGlobalLimitWithoutRetainingFacts()
    {
        var analyzer = Analyze();
        var facts = new List<OperationFact>();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery
            {
                ProfileScope = "Player",
                IncludeKinds = new[] { OperationFactKind.Call },
                MaxFacts = 1,
            },
            fact =>
            {
                facts.Add(fact);
                return true;
            });

        Assert.Single(facts);
        Assert.True(receipt.Truncated);
        Assert.False(receipt.StoppedByConsumer);
        Assert.Equal(1, receipt.EmittedFactCount);
    }

    [Fact]
    public void Scan_SecondaryProfileHonorsConsumerStop()
    {
        var analyzer = Analyze();

        var receipt = analyzer.ScanOperationFacts(
            new OperationFactQuery { ProfileScope = "Player" },
            _ => false);

        Assert.True(receipt.StoppedByConsumer);
        Assert.False(receipt.Truncated);
        Assert.Equal(1, receipt.EmittedFactCount);
        Assert.Equal(1, receipt.CompiledModuleCount);
    }

    [Fact]
    public void IncrementalAnalyze_ChangedLegacyExcludeScopeRejectsInsteadOfReusingWrongSnapshot()
    {
        var analyzer = new RoslynWorkspaceAnalyzer(
            new PhysicalFileSystem(),
            new EditorPlayerResolver());
        analyzer.AnalyzeWorkspace(_root, new AnalysisConfig
        {
            ExcludePatterns = new[] { "Generated" },
            DefineProfiles = new[] { "Editor", "Player" },
            RetainCompilations = true,
        });

        var result = analyzer.IncrementalAnalyze(new AnalysisConfig
        {
            DefineProfiles = new[] { "Editor", "Player" },
            RetainCompilations = true,
        });

        Assert.Equal(IncrementalMode.Rejected, result.Mode);
        Assert.Equal(FallbackReason.AnalysisScopeChanged, result.Reason);
    }

    private RoslynWorkspaceAnalyzer Analyze()
    {
        var analyzer = new RoslynWorkspaceAnalyzer(
            new PhysicalFileSystem(),
            new EditorPlayerResolver());
        analyzer.AnalyzeWorkspace(_root, new AnalysisConfig
        {
            DefineProfiles = new[] { "Editor", "Player" },
            RetainCompilations = true,
        });
        return analyzer;
    }

    private static string? ArgumentConstant(OperationFact fact)
        => Assert.Single(fact.Inputs, input => input.Role == OperationInputRole.Argument)
            .Value.ConstantValue;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class EditorPlayerResolver : IDefineProfileResolver
    {
        public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot) => new[]
        {
            new DefineProfile
            {
                Name = "Editor",
                AddDefines = Array.Empty<string>(),
                RemoveDefines = Array.Empty<string>(),
            },
            new DefineProfile
            {
                Name = "Player",
                AddDefines = new[] { "PLAYER_ONLY" },
                RemoveDefines = Array.Empty<string>(),
            },
        };
    }
}
