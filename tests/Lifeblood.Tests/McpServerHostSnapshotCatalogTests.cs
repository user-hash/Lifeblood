using Lifeblood.Application.UseCases;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

[Collection(McpProcessTestCollection.Name)]
public sealed class McpServerHostSnapshotCatalogTests
{
    private const string LimitVariable = "LIFEBLOOD_SNAPSHOT_HISTORY_LIMIT";
    private const string AgeVariable = "LIFEBLOOD_SNAPSHOT_HISTORY_MAX_AGE_SECONDS";

    [Fact]
    public void ReadSnapshotCatalogOptions_AcceptsBoundedValuesAndDefaultsInvalidValues()
    {
        var priorLimit = Environment.GetEnvironmentVariable(LimitVariable);
        var priorAge = Environment.GetEnvironmentVariable(AgeVariable);
        try
        {
            Environment.SetEnvironmentVariable(LimitVariable, "16");
            Environment.SetEnvironmentVariable(AgeVariable, "0");
            var configured = McpServerHost.ReadSnapshotCatalogOptions();
            Assert.Equal(16, configured.HistoryLimit);
            Assert.Equal(TimeSpan.Zero, configured.MaximumAge);

            Environment.SetEnvironmentVariable(LimitVariable, "17");
            Environment.SetEnvironmentVariable(AgeVariable, "31536001");
            var defaulted = McpServerHost.ReadSnapshotCatalogOptions();
            Assert.Equal(WorkspaceSnapshotCatalogOptions.DefaultHistoryLimit, defaulted.HistoryLimit);
            Assert.Equal(WorkspaceSnapshotCatalogOptions.DefaultMaximumAge, defaulted.MaximumAge);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LimitVariable, priorLimit);
            Environment.SetEnvironmentVariable(AgeVariable, priorAge);
        }
    }
}
