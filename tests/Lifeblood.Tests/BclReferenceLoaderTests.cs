using Lifeblood.Adapters.CSharp.Internal;
using Xunit;

namespace Lifeblood.Tests;

public class BclReferenceLoaderTests
{
    [Fact]
    public void ReferencePackSelection_PrefersExact_ThenLatestUsableNumericVersionOfSameMajor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-reference-packs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var version in new[] { "10.0.9", "10.0.12", "11.0.1", "10.invalid" })
                Directory.CreateDirectory(Path.Combine(root, version, "ref", "net10.0"));
            Directory.CreateDirectory(Path.Combine(root, "10.0.30"));

            Assert.Equal(Path.Combine(root, "10.0.9", "ref", "net10.0"),
                BclReferenceLoader.FindReferenceDirectory(root, "10.0.9", 10));
            Assert.Equal(Path.Combine(root, "10.0.12", "ref", "net10.0"),
                BclReferenceLoader.FindReferenceDirectory(root, "10.0.11", 10));
            Assert.Null(BclReferenceLoader.FindReferenceDirectory(root, "8.0.31", 8));
            Assert.Null(BclReferenceLoader.FindReferenceDirectory(Path.Combine(root, "missing"), "10.0.12", 10));
        }
        finally { Directory.Delete(root, true); }
    }
}
