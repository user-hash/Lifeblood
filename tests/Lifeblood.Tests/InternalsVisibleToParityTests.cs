using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Regression suite for friend-assembly parity between Lifeblood and
/// MSBuild (INV-DIAGNOSTIC-IVT-PARITY-001). Pre-fix, a producer csproj
/// declaring <c>&lt;InternalsVisibleTo Include="Friend" /&gt;</c>
/// emitted no friend-assembly metadata onto its Lifeblood-built PE: the
/// SDK-style source scan skips <c>obj/</c>, so the MSBuild-generated
/// <c>*.AssemblyInfo.cs</c> file carrying the
/// <c>[assembly: InternalsVisibleTo("Friend")]</c> attribute never
/// entered the compilation. The consuming Tests module saw a PE with no
/// IVT and every internal access fired CS0122 — empirically 223 spurious
/// findings on Lifeblood's own test assembly while <c>dotnet build</c>
/// was clean.
///
/// Asserted invariants:
///   1. <see cref="RoslynModuleDiscovery"/> surfaces every
///      <c>&lt;InternalsVisibleTo Include="X" /&gt;</c> item onto
///      <see cref="ModuleInfo.InternalsVisibleTo"/>.
///   2. Items union across multiple item-groups; duplicates dedupe; empty
///      / whitespace-only Includes drop out.
///   3. The compilation seam synthesizes
///      <c>[assembly: InternalsVisibleTo("X")]</c> attributes onto the
///      producer PE so a friend module compiling against the downgraded
///      reference sees the friend-assembly relation and emits zero
///      CS0122 against internal API.
/// </summary>
public class InternalsVisibleToParityTests
{
    [Fact]
    public void ParseProject_SingleInternalsVisibleToItem_SurfacesOnModuleInfo()
    {
        using var tempDir = new TempWorkspace();
        tempDir.WriteFile("Producer.cs", "namespace Producer { internal class Hidden { } }");
        tempDir.WriteFile("Producer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Tests" /></ItemGroup>
            </Project>
            """);

        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var modules = discovery.DiscoverModules(tempDir.Path);

        var producer = Assert.Single(modules);
        var ivt = Assert.Single(producer.InternalsVisibleTo);
        Assert.Equal("Friend.Tests", ivt);
    }

    [Fact]
    public void ParseProject_MultipleInternalsVisibleToItems_UnionAndDedupe()
    {
        using var tempDir = new TempWorkspace();
        tempDir.WriteFile("Producer.cs", "namespace Producer { internal class Hidden { } }");
        tempDir.WriteFile("Producer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Friend.A" />
                <InternalsVisibleTo Include="Friend.B" />
              </ItemGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Friend.A" />
                <InternalsVisibleTo Include="" />
              </ItemGroup>
            </Project>
            """);

        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var modules = discovery.DiscoverModules(tempDir.Path);
        var producer = Assert.Single(modules);

        // Dedupe by ordinal; empty Include drops.
        Assert.Equal(new[] { "Friend.A", "Friend.B" }, producer.InternalsVisibleTo);
    }

    [Fact]
    public void ParseProject_NoInternalsVisibleToItems_DefaultsEmpty()
    {
        using var tempDir = new TempWorkspace();
        tempDir.WriteFile("Producer.cs", "namespace Producer { public class Surface { } }");
        tempDir.WriteFile("Producer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var discovery = new RoslynModuleDiscovery(new PhysicalFileSystem());
        var modules = discovery.DiscoverModules(tempDir.Path);

        var producer = Assert.Single(modules);
        Assert.Empty(producer.InternalsVisibleTo);
    }

    [Fact]
    public void CreateCompilation_WithInternalsVisibleTo_EmitsPeWithIvtAttribute()
    {
        // End-to-end MSBuild-parity proof: a producer module that
        // declares <InternalsVisibleTo Include="Friend" /> must emit a PE
        // carrying [assembly: InternalsVisibleTo("Friend")] so a Roslyn
        // compilation referencing it can see internals. The synthesizer
        // shapes the IVT attribute via the same assembly metadata path
        // MSBuild uses on disk.
        using var tempDir = new TempWorkspace();
        tempDir.WriteFile("Producer.cs", "namespace Producer { internal class Hidden { public Hidden() { } } }");
        tempDir.WriteFile("Producer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
            </Project>
            """);

        var fs = new PhysicalFileSystem();
        var discovery = new RoslynModuleDiscovery(fs);
        var producer = Assert.Single(discovery.DiscoverModules(tempDir.Path));

        CSharpCompilation? producerCompilation = null;
        new ModuleCompilationBuilder(fs).ProcessInOrder(
            new[] { producer },
            tempDir.Path,
            new AnalysisConfig(),
            (m, c) => producerCompilation = c);

        Assert.NotNull(producerCompilation);

        // Producer PE must round-trip [assembly: InternalsVisibleTo("Friend.Consumer")].
        var ivtAttribute = producerCompilation!.Assembly.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute");
        Assert.NotNull(ivtAttribute);
        Assert.Equal("Friend.Consumer", ivtAttribute!.ConstructorArguments[0].Value);
    }

    [Fact]
    public void Consumer_AccessingProducerInternal_AfterIvtSynthesis_EmitsNoCS0122()
    {
        // The friend-assembly contract end-to-end: a consumer named
        // "Friend.Consumer" compiles against the producer's downgraded
        // PE and reaches an internal type. With IVT synthesis applied,
        // Roslyn binds the access without firing CS0122; without
        // synthesis the consumer would see one CS0122 per usage site
        // (the empirical 223-finding class measured on Lifeblood.Tests).
        using var tempDir = new TempWorkspace();
        tempDir.WriteFile("Producer.cs",
            "namespace Producer { internal class Hidden { public static int Get() => 42; } }");
        tempDir.WriteFile("Producer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
            </Project>
            """);

        var fs = new PhysicalFileSystem();
        var discovery = new RoslynModuleDiscovery(fs);
        var producer = Assert.Single(discovery.DiscoverModules(tempDir.Path));

        // Build the producer, emit it to a PE-image MetadataReference (the
        // exact downgrade shape ModuleCompilationBuilder uses in streaming
        // mode), then build a consumer named "Friend.Consumer" that touches
        // the internal type and assert zero CS0122.
        CSharpCompilation? producerCompilation = null;
        new ModuleCompilationBuilder(fs).ProcessInOrder(
            new[] { producer },
            tempDir.Path,
            new AnalysisConfig { RetainCompilations = true },
            (m, c) => producerCompilation = c);
        Assert.NotNull(producerCompilation);

        using var producerPe = new MemoryStream();
        var producerEmit = producerCompilation!.Emit(producerPe);
        Assert.True(producerEmit.Success, "Producer emit failed: "
            + string.Join("; ", producerEmit.Diagnostics.Select(d => d.GetMessage())));
        var producerRef = MetadataReference.CreateFromImage(producerPe.ToArray());

        var consumerTree = CSharpSyntaxTree.ParseText(
            "namespace Friend.Consumer { public class C { public int Reach() => Producer.Hidden.Get(); } }",
            path: "Consumer.cs");

        var consumer = CSharpCompilation.Create(
            "Friend.Consumer",
            new[] { consumerTree },
            new[] { producerRef }.Concat(BclReferenceLoader.References.Value),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var cs0122 = consumer.GetDiagnostics().Where(d => d.Id == "CS0122").ToArray();
        Assert.Empty(cs0122);
    }

    /// <summary>
    /// On-disk scratch directory the discovery layer can scan. Created in
    /// the OS temp directory, written through real <see cref="File"/> I/O
    /// so the tests exercise the exact same parsing path live workspaces
    /// use, deleted on dispose. Mirrors the pattern in
    /// <see cref="BclOwnershipCompilationTests"/>.
    /// </summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; }

        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"lifeblood-ivt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void WriteFile(string relativePath, string content)
            => File.WriteAllText(System.IO.Path.Combine(Path, relativePath), content);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
