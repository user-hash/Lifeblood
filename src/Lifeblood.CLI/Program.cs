using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;

namespace Lifeblood.CLI;

/// <summary>
/// Composition root. Thin dispatch: parse args → resolve adapter → call pipeline → print.
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

    static int RunAnalyze(string[] args)
    {
        var (projectRoot, graphPath, rulesPath) = ParseArgs(args);
        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        var errors = GraphValidator.Validate(graph);
        if (errors.Length > 0)
        {
            foreach (var e in errors.Take(10))
                Console.Error.WriteLine($"  [{e.Code}] {e.Message}");
            return 1;
        }

        var rules = rulesPath != null && File.Exists(rulesPath) ? RulesLoader.Load(rulesPath) : null;
        var analysis = AnalysisPipeline.Run(graph, rules);

        Console.WriteLine($"Symbols: {graph.Symbols.Length}");
        Console.WriteLine($"Edges:   {graph.Edges.Length}");
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
        var (projectRoot, graphPath, _) = ParseArgs(args);
        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        var analysis = AnalysisPipeline.Run(graph);
        var output = new InstructionFileGenerator().Generate(graph, analysis);
        Console.Write(output);
        return 0;
    }

    static int RunExport(string[] args)
    {
        var (projectRoot, graphPath, _) = ParseArgs(args);
        var graph = BuildGraph(projectRoot, graphPath);
        if (graph == null) return 1;

        new JsonGraphExporter().Export(graph, Console.OpenStandardOutput());
        return 0;
    }

    static SemanticGraph? BuildGraph(string? projectRoot, string? graphPath)
    {
        if (graphPath != null)
        {
            if (!File.Exists(graphPath)) { Console.Error.WriteLine($"Not found: {graphPath}"); return null; }
            using var stream = File.OpenRead(graphPath);
            return new JsonGraphImporter().Import(stream);
        }
        if (projectRoot != null)
        {
            var result = new AnalyzeWorkspaceUseCase(new RoslynWorkspaceAnalyzer())
                .Execute(projectRoot, new AnalysisConfig());
            return result.Graph;
        }
        Console.Error.WriteLine("Specify --project <path> or --graph <json>");
        return null;
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
        Console.WriteLine("  lifeblood context --project <path>                         Generate AI context");
        Console.WriteLine("  lifeblood export  --project <path>                         Export graph as JSON");
    }
}
