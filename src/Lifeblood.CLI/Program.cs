using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;

namespace Lifeblood.CLI;

/// <summary>
/// Composition root. Thin dispatch: parse args → build graph → validate → act.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { PrintBanner(); PrintUsage(); return 0; }

        return args[0].ToLowerInvariant() switch
        {
            "analyze" => RunAnalyze(args.Skip(1).ToArray()),
            "context" => RunContext(args.Skip(1).ToArray()),
            "export" => RunExport(args.Skip(1).ToArray()),
            _ => Error($"Unknown command: {args[0]}"),
        };
    }

    /// <summary>
    /// Shared preflight: build graph → validate → return.
    /// Every command goes through this. No command runs on unvalidated input.
    /// </summary>
    static (SemanticGraph? graph, GraphSource source) Preflight(string[] args)
    {
        var (projectRoot, graphPath, _) = ParseArgs(args);
        var source = BuildGraph(projectRoot, graphPath);
        if (source.Graph == null) return (null, source);

        var errors = GraphValidator.Validate(source.Graph);
        if (errors.Length > 0)
        {
            foreach (var e in errors.Take(10))
                Console.Error.WriteLine($"  [{e.Code}] {e.Message}");
            return (null, source);
        }

        return (source.Graph, source);
    }

    static int RunAnalyze(string[] args)
    {
        var (graph, _) = Preflight(args);
        if (graph == null) return 1;

        var (_, _, rulesPath) = ParseArgs(args);
        var rules = rulesPath != null && File.Exists(rulesPath) ? RulesLoader.Load(rulesPath) : null;
        var analysis = AnalysisPipeline.Run(graph, rules);

        Console.WriteLine($"Symbols: {graph.Symbols.Count}");
        Console.WriteLine($"Edges:   {graph.Edges.Count}");
        Console.WriteLine($"Modules: {analysis.Metrics.TotalModules}");
        Console.WriteLine($"Types:   {analysis.Metrics.TotalTypes}");
        if (analysis.Violations.Length > 0)
        {
            Console.WriteLine($"\nViolations: {analysis.Violations.Length}");
            foreach (var v in analysis.Violations)
                Console.WriteLine($"  {v.RuleBroken}");
        }
        if (analysis.Cycles.Length > 0)
            Console.WriteLine($"Cycles: {analysis.Cycles.Length}");

        return analysis.Violations.Length > 0 ? 1 : 0;
    }

    static int RunContext(string[] args)
    {
        var (graph, _) = Preflight(args);
        if (graph == null) return 1;

        var (_, _, rulesPath) = ParseArgs(args);
        var format = args.SkipWhile(a => a != "--format").Skip(1).FirstOrDefault() ?? "json";
        var rules = rulesPath != null && File.Exists(rulesPath) ? RulesLoader.Load(rulesPath) : null;
        var analysis = AnalysisPipeline.Run(graph, rules);

        if (format.Equals("md", StringComparison.OrdinalIgnoreCase)
            || format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(new InstructionFileGenerator().Generate(graph, analysis));
        }
        else
        {
            var useCase = new GenerateContextUseCase(new AgentContextGenerator());
            var pack = useCase.Execute(graph, analysis);
            System.Text.Json.JsonSerializer.Serialize(
                Console.OpenStandardOutput(), pack,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                });
        }
        return 0;
    }

    static int RunExport(string[] args)
    {
        var (graph, source) = Preflight(args);
        if (graph == null) return 1;

        // Preserve language from imported document, default to "csharp" for Roslyn
        var doc = new GraphDocument
        {
            Language = source.Language ?? "csharp",
            Adapter = source.Capability,
            Graph = graph,
        };
        new JsonGraphExporter().Export(doc, Console.OpenStandardOutput());
        return 0;
    }

    static GraphSource BuildGraph(string? projectRoot, string? graphPath)
    {
        if (graphPath != null)
        {
            if (!File.Exists(graphPath))
            {
                Console.Error.WriteLine($"Not found: {graphPath}");
                return new GraphSource();
            }
            using var stream = File.OpenRead(graphPath);
            var doc = new JsonGraphImporter().ImportDocument(stream);
            return new GraphSource
            {
                Graph = doc.Graph,
                Capability = doc.Adapter,
                Language = doc.Language,
            };
        }
        if (projectRoot != null)
        {
            var adapter = new RoslynWorkspaceAnalyzer();
            var result = new AnalyzeWorkspaceUseCase(adapter)
                .Execute(projectRoot, new AnalysisConfig());
            return new GraphSource
            {
                Graph = result.Graph,
                Capability = adapter.Capability,
                Language = "csharp",
            };
        }
        Console.Error.WriteLine("Specify --project <path> or --graph <json>");
        return new GraphSource();
    }

    static (string? project, string? graph, string? rules) ParseArgs(string[] args)
    {
        string? p = null, g = null, r = null;
        for (int i = 0; i < args.Length - 1; i++)
            switch (args[i])
            {
                case "--project": p = args[++i]; break;
                case "--graph": g = args[++i]; break;
                case "--rules": r = args[++i]; break;
            }
        return (p, g, r);
    }

    static int Error(string msg) { Console.Error.WriteLine(msg); return 1; }

    static void PrintBanner()
    {
        Console.WriteLine("Lifeblood — Compiler truth in, AI context out");
        Console.WriteLine("https://github.com/user-hash/Lifeblood");
        Console.WriteLine();
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  lifeblood analyze --project <path>                         Analyze via Roslyn");
        Console.WriteLine("  lifeblood analyze --graph <graph.json>                     Analyze JSON graph");
        Console.WriteLine("  lifeblood analyze --project <path> --rules <rules.json>    Analyze + check rules");
        Console.WriteLine("  lifeblood context --project <path>                         Generate AI context pack (JSON)");
        Console.WriteLine("  lifeblood context --project <path> --format md             Generate instruction file (markdown)");
        Console.WriteLine("  lifeblood export  --project <path>                         Export graph as JSON");
    }
}

/// <summary>
/// Carries graph + metadata from the build step to consumers.
/// Preserves language identity from imported documents.
/// </summary>
internal sealed class GraphSource
{
    public SemanticGraph? Graph { get; init; }
    public Domain.Capabilities.AdapterCapability? Capability { get; init; }
    public string? Language { get; init; }
}
