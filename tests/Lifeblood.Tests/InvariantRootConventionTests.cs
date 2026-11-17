using Lifeblood.Adapters.CSharp;
using Lifeblood.Connectors.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public class InvariantRootConventionTests
{
    [Fact]
    public void Audit_AggregatesPublicAndLegacyInvariantTreesWithSourceAttribution()
    {
        var root = Path.Combine(Path.GetTempPath(), "lifeblood-invariant-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var relative in new[] { "invariants", "docs/invariants" })
                Directory.CreateDirectory(Path.Combine(root, relative));
            var publicPath = Path.Combine(root, "invariants", "architecture.md");
            var legacyPath = Path.Combine(root, "docs", "invariants", "project.md");
            File.WriteAllText(publicPath, "- **INV-PUBLIC-001**: public contributor contract.\n");
            File.WriteAllText(legacyPath, "- **INV-LEGACY-001**: consumer convention remains supported.\n");

            var provider = new LifebloodInvariantProvider(new PhysicalFileSystem());
            var audit = provider.Audit(root);

            Assert.Equal(2, audit.TotalCount);
            Assert.Empty(audit.Duplicates);
            Assert.Equal(publicPath, provider.GetById(root, "INV-PUBLIC-001")!.SourcePath);
            Assert.Equal(legacyPath, provider.GetById(root, "INV-LEGACY-001")!.SourcePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
