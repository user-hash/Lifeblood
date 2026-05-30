using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-ANALYZE-STRUCTURED-FAILURE-001 / LB-INBOX-012. The full analyze pipeline
/// must never let a raw fault escape — a NullReference after Unity asset-import
/// churn surfaced on the wire as bare "Object reference not set to an instance
/// of an object." with no phase / module / file context. The pipeline now wraps
/// any unexpected fault in a <see cref="WorkspaceAnalysisException"/> carrying
/// the progress cursor.
/// </summary>
public class WorkspaceAnalysisFailureTests
{
    [Fact]
    public void AnalyzeWorkspace_PipelineFault_ThrowsStructuredException_WithPhaseContext()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"lb-analyzefault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            // The profile resolver throws during ResolveActiveProfiles, which
            // runs in the discovery phase before any compilation is created.
            var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem(), new ThrowingProfileResolver());

            var ex = Assert.Throws<WorkspaceAnalysisException>(
                () => analyzer.AnalyzeWorkspace(temp, new AnalysisConfig()));

            Assert.Equal("discovery", ex.Phase);
            Assert.True(ex.FailedBeforeCompilation);
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Contains("synthetic profile-resolver fault", ex.Message);
            // The opaque bare loader/NRE-style message is never the whole story.
            Assert.Contains("discovery", ex.Message);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private sealed class ThrowingProfileResolver : IDefineProfileResolver
    {
        public IReadOnlyList<DefineProfile> ResolveProfiles(string projectRoot)
            => throw new InvalidOperationException("synthetic profile-resolver fault");
    }
}
