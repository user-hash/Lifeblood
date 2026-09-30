using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Right;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class BatchCancellationTests
{
    [Fact]
    public void CancelAfterFirstChild_StopsBeforeSecondChildAndReleasesLease()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeblood-batch-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var graphPath = Path.Combine(root, "graph.json");
            File.WriteAllText(graphPath, """
                { "language": "test", "symbols": [{ "id": "type:T", "name": "T", "kind": "Type" }], "edges": [] }
                """);
            var fs = new PhysicalFileSystem();
            using var session = new GraphSession(fs);
            session.Load(null, graphPath, null);
            using var cancellation = new CancellationTokenSource();
            var decorator = new CancelAfterLookup(cancellation);
            var handler = new ToolHandler(session,
                new LifebloodMcpProvider(new BlastRadiusProvider()),
                new LifebloodSymbolResolver(), new LifebloodSemanticSearchProvider(),
                new LifebloodDeadCodeAnalyzer(), new LifebloodPartialViewBuilder(fs),
                new LifebloodInvariantProvider(fs), decorator);
            using var args = JsonDocument.Parse("""
                { "calls": [
                    { "tool": "lifeblood_lookup", "arguments": { "symbolId": "type:T" } },
                    { "tool": "lifeblood_lookup", "arguments": { "symbolId": "type:T" } }
                ] }
                """);

            Assert.Throws<OperationCanceledException>(() => handler.Handle(
                "lifeblood_batch", args.RootElement, cancellation.Token));
            Assert.Equal(1, decorator.LookupCount);
            Assert.Null(handler.Handle("lifeblood_capabilities", null).IsError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CancelAfterLookup(CancellationTokenSource cancellation) : IResponseDecorator
    {
        private readonly LifebloodResponseDecorator _inner = new();
        public int LookupCount { get; private set; }
        public ResponseEnvelope Decorate(string toolName, EnvelopeContext context)
        {
            var envelope = _inner.Decorate(toolName, context);
            if (toolName == "lifeblood_lookup")
            {
                LookupCount++;
                cancellation.Cancel();
            }
            return envelope;
        }
    }

    private sealed class BlastRadiusProvider : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }
}
