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
        IFileSystem fs = new PhysicalFileSystem();
        var session = new GraphSession(fs);
        IBlastRadiusProvider blastRadius = new BlastRadiusBridge();
        // Composition root: concrete adapter/connector types are constructed here
        // and injected into ToolHandler as ports. Per hexagonal invariants, no
        // non-root class may hold a direct reference to LifebloodMcpProvider or
        // LifebloodSymbolResolver. Phase 0 cleanup, 2026-04-11.
        IMcpGraphProvider graphProvider = new LifebloodMcpProvider(blastRadius);
        // Phase 3: the resolver routes every user-supplied identifier through
        // a language-adapter canonicalizer at step 0. For the MCP server the
        // C# adapter is the only language in play, so wire its canonicalizer
        // directly. Future multi-language hosts will pick the canonicalizer
        // based on loaded adapters.
        IUserInputCanonicalizer canonicalizer = new CSharpUserInputCanonicalizer();
        ISymbolResolver resolver = new LifebloodSymbolResolver(canonicalizer);
        // Phase 5: lifeblood_search is backed by a separate port so it can
        // plug in future scorers (BM25, vector embeddings) without touching
        // the existing IMcpGraphProvider surface.
        ISemanticSearchProvider searchProvider = new LifebloodSemanticSearchProvider();
        // Phase 6: dead-code analyzer and partial-view builder. Both live
        // in Connectors.Mcp (not Lifeblood.Analysis, which is Domain-only
        // and cannot reference Application ports). The partial-view
        // builder takes projectRoot as a method parameter at each call,
        // so it's session-state-free.
        IDeadCodeAnalyzer deadCode = new LifebloodDeadCodeAnalyzer();
        IPartialViewBuilder partialView = new LifebloodPartialViewBuilder(fs);
        // Phase 8: invariant provider parses CLAUDE.md at the loaded
        // project root. No graph dependency; pure text-in, data-out.
        // The provider is session-scoped so its per-project-root cache
        // persists across tool calls.
        IInvariantProvider invariants = new LifebloodInvariantProvider(fs);
        var toolHandler = new ToolHandler(
            session, graphProvider, resolver, searchProvider, deadCode, partialView, invariants);
        var dispatcher = new McpDispatcher(session, toolHandler);

        // Graceful shutdown on Ctrl+C or SIGTERM (container/process manager signals)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };

        Console.Error.WriteLine("Lifeblood MCP server starting...");

        using var reader = new StreamReader(Console.OpenStandardInput());

        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break; // stdin closed
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts);
                if (request == null) continue;

                var response = dispatcher.Dispatch(request);
                if (response == null) continue; // Notifications get no response
                var json = JsonSerializer.Serialize(response, JsonOpts);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
            catch (System.Text.Json.JsonException ex)
            {
                // JSON-RPC 2.0: parse error → respond with -32700, id: null
                Console.Error.WriteLine($"Parse error: {ex.Message}");
                var errorResponse = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = -32700, Message = "Parse error" },
                }, JsonOpts);
                Console.WriteLine(errorResponse);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                // JSON-RPC 2.0: internal error → respond with -32603
                Console.Error.WriteLine($"Internal error: {ex.Message}");
                var internalError = JsonSerializer.Serialize(new JsonRpcResponse
                {
                    Error = new JsonRpcError { Code = -32603, Message = $"Internal error: {ex.Message}" },
                }, JsonOpts);
                Console.WriteLine(internalError);
                Console.Out.Flush();
            }
        }

        // Clean up write-side resources (AdhocWorkspace, compilations)
        session.Dispose();
        Console.Error.WriteLine("Lifeblood MCP server stopped.");
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
