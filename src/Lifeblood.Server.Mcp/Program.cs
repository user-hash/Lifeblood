using System.Linq;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Application.Ports.Right.Invariants;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// MCP server composition root. Owns the stdio I/O loop and dependency
/// wiring. All protocol routing lives in <see cref="McpDispatcher"/> so this
/// class stays a thin, non-branching host. Anything with logic more involved
/// than "read line, dispatch, write response" belongs in a dedicated class.
/// </summary>
class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static async Task Main()
    {
        // MCP / JSON-RPC over stdio mandates UTF-8 (per the protocol spec);
        // pin stdin and stdout to UTF-8 explicitly so the host process's
        // codepage (Windows console: typically a non-UTF-8 ANSI codepage)
        // does not silently mangle multi-byte characters in JSON args or
        // responses (e.g. Unicode identifiers, accented characters in
        // search queries). INV-MCP-STDIO-UTF8-001.
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        IFileSystem fs = new PhysicalFileSystem();
        var session = new GraphSession(fs);
        IBlastRadiusProvider blastRadius = new BlastRadiusBridge();
        // Composition root: concrete adapter/connector types are constructed
        // here and injected into ToolHandler as ports. Per hexagonal invariants,
        // no non-root class may hold a direct reference to LifebloodMcpProvider
        // or LifebloodSymbolResolver.
        IMcpGraphProvider graphProvider = new LifebloodMcpProvider(blastRadius);
        // Every user-supplied identifier routes through a language-adapter
        // canonicalizer at resolver step 0. For the MCP server the C# adapter
        // is the only language in play; future multi-language hosts pick the
        // canonicalizer based on loaded adapters.
        IUserInputCanonicalizer canonicalizer = new CSharpUserInputCanonicalizer();
        ISymbolResolver resolver = new LifebloodSymbolResolver(canonicalizer);
        // lifeblood_search lives behind its own port so future scorers (BM25,
        // vector embeddings) plug in without touching IMcpGraphProvider.
        ISemanticSearchProvider searchProvider = new LifebloodSemanticSearchProvider();
        // Dead-code analyzer + partial-view builder live in Connectors.Mcp
        // (not Lifeblood.Analysis, which is Domain-only and cannot reference
        // Application ports). The partial-view builder takes projectRoot as
        // a method parameter at each call, so it's session-state-free.
        // The Unity-aware reachability provider knows Unity's framework-dispatch
        // surface (entrypoint attributes, MonoBehaviour magic methods, lifecycle
        // hooks); non-Unity workspaces still get correct dead-code findings
        // because the adapter returns false for everything (INV-UNITY-001).
        IUnityReachabilityProvider unityReachability = new UnityReachabilityAdapter();
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer(unityReachability);
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(fs);
        var telemetry = DotNetDiagnosticsTelemetrySink.CreateFromEnvironment("LIFEBLOOD_TELEMETRY");
        using var telemetryLifetime = telemetry as IDisposable;
        // Invariant provider parses CLAUDE.md + AGENTS.md + docs/invariants/**.md
        // at the loaded project root. No graph dependency; pure text-in,
        // data-out. Session-scoped so its per-project-root cache persists
        // across tool calls.
        IInvariantProvider invariants = new LifebloodInvariantProvider(fs, telemetry);
        // IResponseDecorator is the single source of truth for the truth
        // envelope attached to every read-side response
        // (INV-ENVELOPE-001). The classification table flows from
        // ToolRegistry — every read-side ToolDefinition declares its own
        // EnvelopeClassification — straight into the decorator at the
        // composition root. Adding a new tool is one entry in the
        // registry; the decorator picks it up automatically. Missing
        // entries fall back to the most-conservative envelope so the
        // audit ratchet (and a real caller) can spot the gap.
        var classifications = ToolRegistry.GetDefinitions()
            .Where(d => d.EnvelopeClassification != null)
            .ToDictionary(d => d.Name, d => d.EnvelopeClassification!, System.StringComparer.Ordinal);

        // INV-ANALYZE-SKIPPED-PROMINENCE-001: thresholds come from
        // environment so the operator can tune them per deployment
        // without code change. Missing / malformed values fall through
        // to the documented StalenessPolicy.Default — a Lifeblood
        // opinion only at the default level, never hardcoded into the
        // decorator path.
        var stalenessPolicy = new StalenessPolicy(
            StalenessSecondsWarnThreshold: ReadEnvLong(
                "LIFEBLOOD_STALENESS_SECONDS_THRESHOLD",
                StalenessPolicy.Default.StalenessSecondsWarnThreshold),
            FilesChangedWarnThreshold: ReadEnvInt(
                "LIFEBLOOD_FILES_CHANGED_THRESHOLD",
                StalenessPolicy.Default.FilesChangedWarnThreshold));
        IResponseDecorator decorator = new LifebloodResponseDecorator(classifications, stalenessPolicy);
        var toolHandler = new ToolHandler(
            session, graphProvider, resolver, searchProvider, deadCode, partialView, invariants, decorator, telemetry: telemetry);
        var dispatcher = new McpDispatcher(session, toolHandler);

        // Graceful shutdown on Ctrl+C or SIGTERM (container/process manager signals)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };

        Console.Error.WriteLine("Lifeblood MCP server starting...");

        using var reader = new StreamReader(Console.OpenStandardInput());
        var strictJson = McpJsonRequestParser.ReadStrictJsonFlag("LIFEBLOOD_STRICT_JSON");

        // The read-dispatch-write loop lives in McpServerLoop so its resilience
        // contract is unit-testable. INV-MCP-TRANSPORT-RESILIENCE-001: no single
        // request fault may close the transport for every other call.
        await McpServerLoop.RunAsync(
            reader,
            Console.Out,
            dispatcher.Dispatch,
            JsonOpts,
            strictJson,
            cts.Token,
            logError: msg => Console.Error.WriteLine(msg));

        // Clean up write-side resources (AdhocWorkspace, compilations)
        session.Dispose();
        Console.Error.WriteLine("Lifeblood MCP server stopped.");
    }

    /// <summary>
    /// Read a long from an environment variable, falling back to the
    /// supplied default when the variable is unset, empty, or fails
    /// to parse. INV-ANALYZE-SKIPPED-PROMINENCE-001.
    /// </summary>
    private static long ReadEnvLong(string name, long fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Read an int from an environment variable, falling back to the
    /// supplied default when the variable is unset, empty, or fails
    /// to parse. INV-ANALYZE-SKIPPED-PROMINENCE-001.
    /// </summary>
    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Composition root bridge: delegates to Analysis.BlastRadiusAnalyzer
    /// without letting Connectors.Mcp depend on Analysis directly.
    /// </summary>
    private sealed class BlastRadiusBridge : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => Analysis.BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}
