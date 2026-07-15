using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

public class AcceptedChangeSetTests
{
    [Fact]
    public void Create_NormalizesPathsAndDerivesEveryCountFromOneSet()
    {
        var changes = AcceptedChangeSet.Create(
            ChangeScanMode.AuthoritativeChangedSet,
            reanalyzedSourceFiles: new[] { @"./src\B.cs", "src/A.cs", "src/A.cs" },
            mtimeTouchedSourceFiles: new[] { "src/A.cs", "src/Touch.cs" },
            contentChangedSourceFiles: new[] { "src/A.cs" },
            descriptorForcedSourceFiles: new[] { "src/B.cs" },
            deletedSourceFiles: new[] { "src/Deleted.cs" });

        Assert.Equal(ChangeScanMode.AuthoritativeChangedSet, changes.ScanMode);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, changes.ReanalyzedSourceFiles);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs", "src/Deleted.cs" }, changes.ChangedSourceFiles);
        Assert.Equal(3, changes.ChangedFileCount);
        Assert.Equal(2, changes.MtimeTouchedFileCount);
        Assert.Equal(1, changes.ContentChangedFileCount);
        Assert.Equal(
            new[] { "src/A.cs", "src/B.cs", "src/Deleted.cs", "src/Touch.cs" },
            changes.EvidenceSourceFiles);
    }

    [Fact]
    public void Create_RejectsContradictoryOrNonRelativeEvidence()
    {
        Assert.Throws<ArgumentException>(() => AcceptedChangeSet.Create(
            ChangeScanMode.FilesystemPrefilter,
            contentChangedSourceFiles: new[] { "src/Orphan.cs" }));
        Assert.Throws<ArgumentException>(() => AcceptedChangeSet.Create(
            ChangeScanMode.FilesystemPrefilter,
            reanalyzedSourceFiles: new[] { "src/A.cs" },
            deletedSourceFiles: new[] { "src/A.cs" }));
        Assert.Throws<ArgumentException>(() => AcceptedChangeSet.Create(
            ChangeScanMode.FilesystemPrefilter,
            reanalyzedSourceFiles: new[] { "../Outside.cs" }));
    }

    [Fact]
    public void Create_RequiresFullFallbackFlagAndModeToAgree()
    {
        Assert.Throws<ArgumentException>(() => AcceptedChangeSet.Create(
            ChangeScanMode.FullFallback));
        Assert.Throws<ArgumentException>(() => AcceptedChangeSet.Create(
            ChangeScanMode.FilesystemPrefilter,
            fullFallback: true));

        var changes = AcceptedChangeSet.Create(
            ChangeScanMode.FullFallback,
            fullFallback: true,
            reanalyzedSourceFiles: new[] { "src/A.cs" });
        Assert.True(changes.FullFallback);
        Assert.Equal(1, changes.ChangedFileCount);
        Assert.Empty(changes.MtimeTouchedSourceFiles);
        Assert.Empty(changes.ContentChangedSourceFiles);
    }
}
