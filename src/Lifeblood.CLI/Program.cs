using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Domain.Rules;

namespace Lifeblood.CLI;

/// <summary>
/// Composition root. Wires left side to right side.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintBanner();
            PrintUsage();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "analyze" => RunAnalyze(args.Skip(1).ToArray()),
            "context" => RunContext(args.Skip(1).ToArray()),
            "export" => RunExport(args.Skip(1).ToArray()),
            _ => Error($"Unknown command: {args[0]}"),
        };
    }

    static int RunAnalyze(string[] args)
    {
        var (projectRoot, graphPath, rulesPath) = ParseArgs(args);

        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        // Validate graph
        var errors = GraphValidator.Validate(graph);
        if (errors.Length > 0)
        {
            Console.Error.WriteLine($"Graph validation: {errors.Length} errors");
            foreach (var e in errors.Take(10))
                Console.Error.WriteLine($"  [{e.Code}] {e.Message}");
            return 1;
        }

        // Run analysis
        var analysis = RunAnalysis(graph, rulesPath);

        // Report
        Console.WriteLine($"Symbols: {graph.Symbols.Length}");
        Console.WriteLine($"Edges:   {graph.Edges.Length}");
        Console.WriteLine($"Modules: {graph.Symbols.Count(s => s.Kind == SymbolKind.Module)}");
        Console.WriteLine($"Types:   {graph.Symbols.Count(s => s.Kind == SymbolKind.Type)}");

        if (analysis.Violations.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Violations: {analysis.Violations.Length}");
            foreach (var v in analysis.Violations)
                Console.WriteLine($"  {v.RuleBroken}");
        }

        if (analysis.Cycles.Length > 0)
            Console.WriteLine($"Cycles: {analysis.Cycles.Length}");

        return analysis.Violations.Length > 0 ? 1 : 0;
    }

    static int RunContext(string[] args)
    {
        var (projectRoot, graphPath, _) = ParseArgs(args);

        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        var analysis = RunAnalysis(graph, null);

        // Generate instruction file (markdown)
        var generator = new InstructionFileGenerator();
        var output = generator.Generate(graph, analysis);
        Console.Write(output);

        return 0;
    }

    static int RunExport(string[] args)
    {
        var (projectRoot, graphPath, _) = ParseArgs(args);

        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        var exporter = new JsonGraphExporter();
        exporter.Export(graph, Console.OpenStandardOutput());

        return 0;
    }

    static SemanticGraph? BuildGraph(string? projectRoot, string? graphPath)
    {
        if (graphPath != null)
        {
            // JSON graph import
            if (!File.Exists(graphPath))
            {
                Console.Error.WriteLine($"Graph file not found: {graphPath}");
                return null;
            }

            var importer = new JsonGraphImporter();
            using var stream = File.OpenRead(graphPath);
            return importer.Import(stream);
        }

        if (projectRoot != null)
        {
            // Roslyn adapter
            var adapter = new RoslynWorkspaceAnalyzer();
            var config = new AnalysisConfig();
            var useCase = new AnalyzeWorkspaceUseCase(adapter);
            var result = useCase.Execute(projectRoot, config);
            return result.Graph;
        }

        Console.Error.WriteLine("Specify --project <path> or --graph <json>");
        return null;
    }

    static AnalysisResult RunAnalysis(SemanticGraph graph, string? rulesPath)
    {
        // Coupling
        var coupling = CouplingAnalyzer.Analyze(graph,
            new[] { SymbolKind.Type, SymbolKind.Module });

        // Circular dependencies
        var cycles = CircularDependencyDetector.Detect(graph);

        // Tier classification
        var tiers = TierClassifier.Classify(graph);

        // Blast radius for high-fan-in symbols
        var blastRadii = coupling
            .Where(c => c.FanIn >= 3)
            .Select(c => BlastRadiusAnalyzer.Analyze(graph, c.SymbolId))
            .ToArray();

        // Rule validation
        Violation[] violations = Array.Empty<Violation>();
        if (rulesPath != null && File.Exists(rulesPath))
        {
            // Load rules from JSON
            var rulesJson = File.ReadAllText(rulesPath);
            var rules = System.Text.Json.JsonSerializer.Deserialize<RulesDocument>(rulesJson,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });
            if (rules?.Rules != null)
                violations = RuleValidator.Validate(graph, rules.Rules);
        }

        return new AnalysisResult
        {
            Coupling = coupling,
            Violations = violations,
            Tiers = tiers,
            Cycles = cycles,
            BlastRadii = blastRadii,
            Metrics = new GraphMetrics
            {
                TotalSymbols = graph.Symbols.Length,
                TotalEdges = graph.Edges.Length,
                TotalFiles = graph.Symbols.Count(s => s.Kind == SymbolKind.File),
                TotalTypes = graph.Symbols.Count(s => s.Kind == SymbolKind.Type),
                TotalModules = graph.Symbols.Count(s => s.Kind == SymbolKind.Module),
                ViolationCount = violations.Length,
                CycleCount = cycles.Length,
            },
        };
    }

    static (string? projectRoot, string? graphPath, string? rulesPath) ParseArgs(string[] args)
    {
        string? project = null, graph = null, rules = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--project": project = args[++i]; break;
                case "--graph": graph = args[++i]; break;
                case "--rules": rules = args[++i]; break;
            }
        }
        return (project, graph, rules);
    }

    static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

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
        Console.WriteLine("  lifeblood context --project <path>                         Generate AI context");
        Console.WriteLine("  lifeblood export  --project <path>                         Export graph as JSON");
    }
}

internal sealed class RulesDocument
{
    public ArchitectureRule[]? Rules { get; set; }
}
