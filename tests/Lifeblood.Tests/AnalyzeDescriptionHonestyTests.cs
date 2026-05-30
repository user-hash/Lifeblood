using System.Linq;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001 / LB-TRACK-20260530-028.
/// The <c>lifeblood_analyze</c> tool description is the public contract for
/// how file discovery behaves. DAWG dogfood proved the old prose over-promised
/// ("a .cs file ... WILL be picked up by Lifeblood's incremental walker") —
/// Lifeblood discovers files through generated project descriptors, so a
/// pre-import file stays invisible until the descriptors regenerate. This is a
/// source-text ratchet because the contract under test IS the description
/// string; it must not silently drift back to the over-promise.
/// </summary>
public class AnalyzeDescriptionHonestyTests
{
    private static string AnalyzeDescription =>
        ToolRegistry.GetDefinitions().Single(d => d.Name == "lifeblood_analyze").Description;

    [Fact]
    public void AnalyzeDescription_DoesNotOverpromisePreMetaDiscovery()
    {
        // The exact over-promise observed under DAWG dogfood. Its return is a
        // regression; assert the false claim is gone in both phrasings.
        Assert.DoesNotContain("WILL be picked up", AnalyzeDescription);
        Assert.DoesNotContain("picked up by Lifeblood's incremental walker", AnalyzeDescription);
    }

    [Fact]
    public void AnalyzeDescription_StatesDescriptorDrivenDiscovery_AndStaleDescriptorPath()
    {
        var description = AnalyzeDescription;
        Assert.Contains("project descriptors", description);
        // Names the recommended pre-import path through compile_check's typed signal.
        Assert.Contains("staleDescriptorHint", description);
        Assert.Contains("INV-UNITY-NEWFILE-DISCOVERY-HONESTY-001", description);
    }
}
