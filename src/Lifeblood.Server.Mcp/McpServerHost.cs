using System.Linq;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Owns the MCP server composition graph. Program chooses the transport
/// (stdio or shared named-pipe daemon); this type owns the semantic session
/// and tool wiring used by either transport.
/// </summary>
internal sealed class McpServerHost : IDisposable
{
    private readonly IDisposable? _telemetryLifetime;
    private readonly GraphSessionGate _sessionGate;
    private readonly AnalysisRequestCoordinator<McpToolResult> _analysisCoordinator;

    private McpServerHost(
        GraphSession session,
        McpDispatcher dispatcher,
        IDisposable? telemetryLifetime,
        GraphSessionGate sessionGate,
        AnalysisRequestCoordinator<McpToolResult> analysisCoordinator)
    {
        Session = session;
        Dispatcher = dispatcher;
        _telemetryLifetime = telemetryLifetime;
        _sessionGate = sessionGate;
        _analysisCoordinator = analysisCoordinator;
    }

    public GraphSession Session { get; }

    public McpDispatcher Dispatcher { get; }

    public int InFlightAnalysisCount => _analysisCoordinator.InFlightCount;

    public static McpServerHost Create(
        ToolJsonCompatibilityMode jsonCompatibilityMode,
        string? boundWorkspaceRoot = null,
        ISharedDaemonStatusProvider? sharedDaemonStatus = null)
    {
        var telemetry = DotNetDiagnosticsTelemetrySink.CreateFromEnvironment("LIFEBLOOD_TELEMETRY");
        var telemetryLifetime = telemetry as IDisposable;
        IFileSystem fs = new PhysicalFileSystem();
        var snapshotCatalog = new WorkspaceSnapshotCatalog(ReadSnapshotCatalogOptions());
        var session = new GraphSession(fs, telemetry, snapshotCatalog);
        IBlastRadiusProvider blastRadius = new BlastRadiusBridge();
        IMcpGraphProvider graphProvider = new LifebloodMcpProvider(blastRadius);
        IUserInputCanonicalizer canonicalizer = new CSharpUserInputCanonicalizer();
        ISymbolResolver resolver = new LifebloodSymbolResolver(canonicalizer);
        ISemanticSearchProvider searchProvider = new LifebloodSemanticSearchProvider();
        IUnityReachabilityProvider unityReachability = new UnityReachabilityAdapter();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer(unityReachability);
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(fs);
        IInvariantProvider invariants = new LifebloodInvariantProvider(fs, telemetry);

        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, StringComparer.Ordinal);

        var stalenessPolicy = new StalenessPolicy(
            StalenessSecondsWarnThreshold: ReadEnvLong(
                "LIFEBLOOD_STALENESS_SECONDS_THRESHOLD",
                StalenessPolicy.Default.StalenessSecondsWarnThreshold),
            FilesChangedWarnThreshold: ReadEnvInt(
                "LIFEBLOOD_FILES_CHANGED_THRESHOLD",
                StalenessPolicy.Default.FilesChangedWarnThreshold));
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications, stalenessPolicy);
        var sessionGate = new GraphSessionGate(session);
        var analysisCoordinator = new AnalysisRequestCoordinator<McpToolResult>();
        var toolHandler = new ToolHandler(
            session,
            graphProvider,
            resolver,
            searchProvider,
            deadCode,
            partialView,
            invariants,
            decorator,
            telemetry: telemetry,
            jsonCompatibilityMode: jsonCompatibilityMode,
            sessionGate: sessionGate,
            boundWorkspaceRoot: boundWorkspaceRoot,
            analysisCoordinator: analysisCoordinator,
            sharedDaemonStatus: sharedDaemonStatus);
        var dispatcher = new McpDispatcher(toolHandler);

        return new McpServerHost(
            session,
            dispatcher,
            telemetryLifetime,
            sessionGate,
            analysisCoordinator);
    }

    public void Dispose()
    {
        _analysisCoordinator.Dispose();
        Session.Dispose();
        _sessionGate.Dispose();
        _telemetryLifetime?.Dispose();
    }

    internal static WorkspaceSnapshotCatalogOptions ReadSnapshotCatalogOptions()
    {
        var historyLimit = ReadEnvInt(
            "LIFEBLOOD_SNAPSHOT_HISTORY_LIMIT",
            WorkspaceSnapshotCatalogOptions.DefaultHistoryLimit);
        if (historyLimit is < 0 or > WorkspaceSnapshotCatalogOptions.HardMaximumHistoryLimit)
            historyLimit = WorkspaceSnapshotCatalogOptions.DefaultHistoryLimit;

        var historyAgeSeconds = ReadEnvInt(
            "LIFEBLOOD_SNAPSHOT_HISTORY_MAX_AGE_SECONDS",
            checked((int)WorkspaceSnapshotCatalogOptions.DefaultMaximumAge.TotalSeconds));
        if (historyAgeSeconds is < 0 or > 31_536_000)
            historyAgeSeconds = checked((int)WorkspaceSnapshotCatalogOptions.DefaultMaximumAge.TotalSeconds);

        return new WorkspaceSnapshotCatalogOptions(
            historyLimit,
            TimeSpan.FromSeconds(historyAgeSeconds));
    }

    private static long ReadEnvLong(string name, long fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private sealed class BlastRadiusBridge : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => Analysis.BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}
