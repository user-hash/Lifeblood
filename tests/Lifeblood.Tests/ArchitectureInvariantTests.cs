using System.Xml.Linq;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchet tests that enforce hexagonal architecture invariants.
/// These prevent accidental dependency violations from ever shipping.
/// </summary>
public class ArchitectureInvariantTests
{
    private static readonly string SrcRoot = FindSrcRoot();

    [Fact]
    public void Domain_HasZeroDependencies()
    {
        var csproj = Path.Combine(SrcRoot, "Lifeblood.Domain", "Lifeblood.Domain.csproj");
        if (!File.Exists(csproj)) return; // skip if not in repo context

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
        if (!File.Exists(csproj)) return;

        var doc = XDocument.Load(csproj);
        var projectRefs = doc.Descendants("ProjectReference")
            .Select(el => el.Attribute("Include")?.Value ?? "")
            .ToArray();
        var packageRefs = doc.Descendants("PackageReference").ToArray();

        Assert.All(projectRefs, r => Assert.Contains("Domain", r)); // INV-APP-001
        Assert.Empty(packageRefs);
    }

    [Fact]
    public void Adapters_DoNotReferenceOtherAdapters()
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

            foreach (var r in refs)
                Assert.DoesNotContain("Adapters.", r.Replace(Path.GetFileName(csproj), ""));
        }
    }

    [Fact]
    public void Connectors_DoNotReferenceAdapters()
    {
        var connectorDirs = Directory.GetDirectories(SrcRoot, "Lifeblood.Connectors.*");
        foreach (var dir in connectorDirs)
        {
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (csproj == null) continue;

            var doc = XDocument.Load(csproj);
            var refs = doc.Descendants("ProjectReference")
                .Select(el => el.Attribute("Include")?.Value ?? "")
                .ToArray();

            foreach (var r in refs)
                Assert.DoesNotContain("Adapters", r);
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
