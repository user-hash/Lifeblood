using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>INV-MULTI-DEFINE-IOP-001 — Wave 6.E.</summary>
public class MultiProfileIOpScopeTests
{
    [Fact]
    public void RetainedProfileName_IsNull_BeforeAnyAnalyze()
    {
        var analyzer = new RoslynWorkspaceAnalyzer(new PhysicalFileSystem(), new DefaultDefineProfileResolver());

        Assert.Null(analyzer.RetainedProfileName);
    }

    [Fact]
    public void RetainedProfileName_EqualsFirstActiveProfile_AfterSingleProfileAnalyze()
    {
        var fs = new PhysicalFileSystem();
        var analyzer = new RoslynWorkspaceAnalyzer(fs, new DefaultDefineProfileResolver());
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifeblood-iop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            analyzer.AnalyzeWorkspace(tempDir, new AnalysisConfig());

            Assert.Equal(DefaultDefineProfileResolver.EditorProfileName, analyzer.RetainedProfileName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RetainedProfileName_EqualsFirstActiveProfile_UnderMultiProfileAnalyze()
    {
        var fs = new PhysicalFileSystem();
        var unityDir = Path.Combine(Path.GetTempPath(), $"lifeblood-iop-unity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(unityDir, "Library"));
        try
        {
            var analyzer = new RoslynWorkspaceAnalyzer(fs, new UnityDefineProfileResolver(fs));

            analyzer.AnalyzeWorkspace(unityDir, new AnalysisConfig
            {
                DefineProfiles = new[] { "Editor", "Player" },
            });

            // Retained = first profile = Editor.
            Assert.Equal("Editor", analyzer.RetainedProfileName);
        }
        finally
        {
            Directory.Delete(unityDir, recursive: true);
        }
    }

    [Fact]
    public void RetainedProfileName_RespectsCallerProfileOrder_PlayerFirst()
    {
        var fs = new PhysicalFileSystem();
        var unityDir = Path.Combine(Path.GetTempPath(), $"lifeblood-iop-unity-pf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(unityDir, "Library"));
        try
        {
            var analyzer = new RoslynWorkspaceAnalyzer(fs, new UnityDefineProfileResolver(fs));

            analyzer.AnalyzeWorkspace(unityDir, new AnalysisConfig
            {
                DefineProfiles = new[] { "Player", "Editor" },
            });

            // Retained = first profile in caller's list.
            Assert.Equal("Player", analyzer.RetainedProfileName);
        }
        finally
        {
            Directory.Delete(unityDir, recursive: true);
        }
    }
}
