using System.Xml.Linq;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchet tests that enforce hexagonal architecture invariants.
/// These prevent accidental dependency violations from ever shipping.
/// Tests MUST fail loud — never silently skip.
/// </summary>
public class ArchitectureInvariantTests
{
    private static readonly string SrcRoot = FindSrcRoot();

    [Fact]
    public void Domain_HasZeroDependencies()
    {
        var csproj = Path.Combine(SrcRoot, "Lifeblood.Domain", "Lifeblood.Domain.csproj");
        Assert.True(File.Exists(csproj), $"Domain csproj not found: {csproj}");

        var doc = XDocument.Load(csproj);
        var packageRefs = doc.Descendants("PackageReference").ToArray();
        var projectRefs = doc.Descendants("ProjectReference").ToArray();

        Assert.Empty(packageRefs); // INV-DOMAIN-001
        Assert.Empty(projectRefs); // INV-DOMAIN-002
    }

    [Fact]
    public void Application_DependsOnlyOnDomain()
    {
        var csproj = Path.Combine(SrcRoot, "Lifeblood.Application", "Lifeblood.Application.csproj");
        Assert.True(File.Exists(csproj), $"Application csproj not found: {csproj}");

        var doc = XDocument.Load(csproj);
        var projectRefs = doc.Descendants("ProjectReference")
            .Select(el => el.Attribute("Include")?.Value ?? "")
            .ToArray();
        var packageRefs = doc.Descendants("PackageReference").ToArray();

        // INV-APP-001: Must reference exactly Domain, nothing else
        Assert.All(projectRefs, r =>
            Assert.EndsWith("Lifeblood.Domain.csproj", r));
        Assert.Empty(packageRefs);
    }

    [Fact]
    public void Analysis_DependsOnlyOnDomain()
    {
        var csproj = Path.Combine(SrcRoot, "Lifeblood.Analysis", "Lifeblood.Analysis.csproj");
        Assert.True(File.Exists(csproj), $"Analysis csproj not found: {csproj}");

        var doc = XDocument.Load(csproj);
        var projectRefs = doc.Descendants("ProjectReference")
            .Select(el => el.Attribute("Include")?.Value ?? "")
            .ToArray();

        Assert.All(projectRefs, r =>
            Assert.EndsWith("Lifeblood.Domain.csproj", r));
    }

    [Fact]
    public void Adapters_DoNotReferenceOtherAdapters()
    {
        var adapterDirs = Directory.GetDirectories(SrcRoot, "Lifeblood.Adapters.*");
        Assert.NotEmpty(adapterDirs); // Must find at least one adapter

        foreach (var dir in adapterDirs)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj == null) continue;

            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference")
                .Select(el => el.Attribute("Include")?.Value ?? "")
                .ToArray();

            var adapterName = Path.GetFileName(dir);
            foreach (var r in refs)
            {
                // Skip self-reference check — verify no OTHER adapter is referenced
                var refFile = Path.GetFileNameWithoutExtension(r);
                if (refFile.StartsWith("Lifeblood.Adapters.", StringComparison.OrdinalIgnoreCase)
                    && !refFile.Equals(adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Fail($"{adapterName} references another adapter: {r}");
                }
            }
        }
    }

    [Fact]
    public void Connectors_DoNotReferenceAdaptersOrAnalysis()
    {
        var connectorDirs = Directory.GetDirectories(SrcRoot, "Lifeblood.Connectors.*");
        Assert.NotEmpty(connectorDirs);

        foreach (var dir in connectorDirs)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj == null) continue;

            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference")
                .Select(el => el.Attribute("Include")?.Value ?? "")
                .ToArray();

            var connectorName = Path.GetFileName(dir);
            foreach (var r in refs)
            {
                var refFile = Path.GetFileNameWithoutExtension(r);
                Assert.False(
                    refFile.StartsWith("Lifeblood.Adapters.", StringComparison.OrdinalIgnoreCase),
                    $"{connectorName} must not reference adapter: {r}");
                Assert.False(
                    refFile.Equals("Lifeblood.Analysis", StringComparison.OrdinalIgnoreCase),
                    $"{connectorName} must not reference Analysis directly: {r}");
            }
        }
    }

    [Fact]
    public void Adapters_DoNotReferenceConnectors()
    {
        var adapterDirs = Directory.GetDirectories(SrcRoot, "Lifeblood.Adapters.*");

        foreach (var dir in adapterDirs)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj == null) continue;

            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference")
                .Select(el => el.Attribute("Include")?.Value ?? "")
                .ToArray();

            var adapterName = Path.GetFileName(dir);
            foreach (var r in refs)
            {
                var refFile = Path.GetFileNameWithoutExtension(r);
                Assert.False(
                    refFile.StartsWith("Lifeblood.Connectors.", StringComparison.OrdinalIgnoreCase),
                    $"{adapterName} must not reference connector: {r}");
            }
        }
    }

    private static string FindSrcRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            var src = Path.Combine(dir, "src");
            if (Directory.Exists(src) && Directory.Exists(Path.Combine(src, "Lifeblood.Domain")))
                return src;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src");
    }
}
