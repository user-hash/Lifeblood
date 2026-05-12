using Lifeblood.Adapters.CSharp;
using Lifeblood.Adapters.JsonGraph;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.ContextPack;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;

namespace Lifeblood.CLI;

/// <summary>
/// Composition root. Thin dispatch: parse args → build graph → validate → act.
/// </summary>
class Program
{
    private static readonly IFileSystem Fs = new PhysicalFileSystem();
    private static readonly RulesLoader Rules = new(Fs);
    private static readonly ConsoleProgressSink Progress = new();
    private static readonly IUsageProbe UsageProbe = new ProcessUsageProbe();
    private static AnalysisUsage? LastUsage;

    static int Main(string[] args)
    {
        if (args.Length == 0) { PrintBanner(); PrintUsage(); return 0; }

        return args[0].ToLowerInvariant() switch
        {
            "analyze" => RunAnalyze(args.Skip(1).ToArray()),
            "context" => RunContext(args.Skip(1).ToArray()),
            "export" => RunExport(args.Skip(1).ToArray()),
            "verify" => RunVerify(args.Skip(1).ToArray()),
            // Standard help arms — convention is that any of these prints
            // usage and exits 0. Pre-fix the help arms hit the unknown-
            // command branch and exited 1 with a misleading "Unknown command"
            // message, which is bad public-CLI hygiene per the v0.7.2
            // pre-tag review.
            "--help" or "-h" or "/?" or "help"
                => RunHelp(),
            _ => Error($"Unknown command: {args[0]}"),
        };
    }

    static int RunHelp()
    {
        PrintBanner();
        PrintUsage();
        return 0;
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
        // CLI is single-shot — there is no persistent adapter snapshot
        // across process boundaries, so incremental analyze is structurally
        // incoherent here (every first call would NoPriorAnalysis-reject
        // regardless of the workspace state). Be honest about it: refuse
        // the flag and point at the two paths that ARE coherent. INV-ANALYZE-
        // FALLBACK-001's caller-owned scope policy applies in the MCP layer
        // where the GraphSession holds a long-lived snapshot.
        if (HasFlag(args, "--incremental"))
        {
            Console.Error.WriteLine(
                "CLI analyze is single-shot; incremental analyze requires a persistent snapshot.");
            Console.Error.WriteLine(
                "Use the MCP server (lifeblood-mcp) for interactive incremental analyze across many calls,");
            Console.Error.WriteLine(
                "or `lifeblood verify --incremental --project <path>` for a one-shot drift check");
            Console.Error.WriteLine(
                "(runs full + incremental in one process and asserts INV-INCREMENTAL-XREF-001 holds).");
            return 1;
        }

        var (graph, _) = Preflight(args);
        if (graph == null) return 1;

        var (_, _, rulesPath) = ParseArgs(args);
        var rules = rulesPath != null ? Rules.LoadRules(rulesPath) : null;
        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);

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

        if (LastUsage != null)
            PrintUsageBlock(LastUsage);

        return analysis.Violations.Length > 0 ? 1 : 0;
    }

    /// <summary>
    /// Renders the runtime usage snapshot in a single fixed-column block so
    /// it is trivially scannable by humans and parseable by agents. The block
    /// is emitted to stderr so it does not pollute stdout consumers that may
    /// be piping graph output downstream. All numbers are formatted with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> so the
    /// output reads the same regardless of the host's regional settings.
    /// </summary>
    static void PrintUsageBlock(AnalysisUsage u)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var avgPerCore = u.HostLogicalCores > 0
            ? u.CpuUtilizationPercent / u.HostLogicalCores
            : 0.0;
        var peakWsMb = u.PeakWorkingSetBytes / 1024.0 / 1024.0;
        var peakPrivateMb = u.PeakPrivateBytesBytes / 1024.0 / 1024.0;
        var wallSec = u.WallTimeMs / 1000.0;

        var err = Console.Error;
        err.WriteLine();
        err.WriteLine("── usage ─────────────────────────────────────────────────");
        err.WriteLine(string.Format(inv, "  Wall time          : {0,10:N0} ms  ({1:N1} s)", u.WallTimeMs, wallSec));
        err.WriteLine(string.Format(inv, "  CPU total          : {0,10:N0} ms", u.CpuTimeTotalMs));
        err.WriteLine(string.Format(inv, "    user mode        : {0,10:N0} ms", u.CpuTimeUserMs));
        err.WriteLine(string.Format(inv, "    kernel mode      : {0,10:N0} ms", u.CpuTimeKernelMs));
        err.WriteLine(string.Format(inv, "  CPU utilization    : {0,9:N1}% of one core", u.CpuUtilizationPercent));
        err.WriteLine(string.Format(inv, "  CPU avg per core   : {0,9:N1}% across {1} logical cores", avgPerCore, u.HostLogicalCores));
        err.WriteLine(string.Format(inv, "  Peak working set   : {0,10:N0} MB", peakWsMb));
        err.WriteLine(string.Format(inv, "  Peak private bytes : {0,10:N0} MB", peakPrivateMb));
        err.WriteLine(string.Format(inv, "  GC collections     : gen0={0}  gen1={1}  gen2={2}", u.GcGen0Collections, u.GcGen1Collections, u.GcGen2Collections));
        if (u.Phases.Length > 0)
        {
            err.WriteLine("  Phases             :");
            foreach (var p in u.Phases)
                err.WriteLine(string.Format(inv, "    {0,-18} : {1,10:N0} ms", p.Name, p.DurationMs));
        }
        err.WriteLine("──────────────────────────────────────────────────────────");
    }

    static int RunContext(string[] args)
    {
        var (graph, _) = Preflight(args);
        if (graph == null) return 1;

        var (_, _, rulesPath) = ParseArgs(args);
        var format = args.SkipWhile(a => a != "--format").Skip(1).FirstOrDefault() ?? "json";
        var rules = rulesPath != null ? Rules.LoadRules(rulesPath) : null;
        var analysis = Lifeblood.Analysis.AnalysisPipeline.Run(graph, rules);

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

        // --out <path> writes to a file; otherwise stream to stdout.
        // File-mode goes through IFileSystem so the path is created with
        // the same atomicity / permissions semantics as every other
        // Lifeblood file write.
        var outPath = ParseFlagValue(args, "--out");
        if (outPath != null)
        {
            using var stream = Fs.OpenWrite(outPath);
            new JsonGraphExporter().Export(doc, stream);
            Console.Error.WriteLine($"Exported graph to {outPath}");
        }
        else
        {
            new JsonGraphExporter().Export(doc, Console.OpenStandardOutput());
        }
        return 0;
    }

    /// <summary>
    /// `lifeblood verify` runs one of several drift / regression checks
    /// against a workspace, each behind an explicit subflag. Subflag
    /// rather than positional argument so future verify modes
    /// (`--schema`, `--determinism`, etc.) compose cleanly. Today the
    /// only mode is <c>--incremental</c>: the
    /// <c>INV-INCREMENTAL-XREF-001</c> acceptance criterion as a CLI
    /// regression check.
    /// </summary>
    static int RunVerify(string[] args)
    {
        if (HasFlag(args, "--incremental"))
            return RunVerifyIncremental(args);

        Console.Error.WriteLine("verify requires a mode flag:");
        Console.Error.WriteLine("  --incremental    full vs incremental edge-count drift check (INV-INCREMENTAL-XREF-001)");
        return 1;
    }

    /// <summary>
    /// Verifies the cross-module-edge integrity acceptance criterion for
    /// <c>INV-INCREMENTAL-XREF-001</c>: a full analyze followed by an
    /// incremental analyze on the same source tree (no file changes
    /// between the two calls) MUST produce identical <c>summary.edges</c>.
    /// Pre-fix (LB-BUG-020), incremental dropped cross-module edges
    /// silently in proportion to the unchanged-module fan-in.
    ///
    /// Single-process so the adapter snapshot is shared between the two
    /// calls. Useful as a regression check any consumer can run against
    /// their own workspace; non-zero exit on drift makes it CI-wireable.
    /// </summary>
    static int RunVerifyIncremental(string[] args)
    {
        var projectRoot = ParseFlagValue(args, "--project");
        if (projectRoot == null)
        {
            Console.Error.WriteLine("verify --incremental requires --project <path>");
            return 1;
        }

        var adapter = new RoslynWorkspaceAnalyzer(Fs);

        Console.WriteLine("[1/2] Full analyze...");
        var fullGraph = adapter.AnalyzeWorkspace(projectRoot, new AnalysisConfig());
        var fullSymbols = fullGraph.Symbols.Count;
        var fullEdges = fullGraph.Edges.Count;
        Console.WriteLine($"      Symbols: {fullSymbols,8}  Edges: {fullEdges,8}");

        Console.WriteLine("[2/2] Incremental re-analyze (no file changes expected)...");
        var incremental = adapter.IncrementalAnalyze(new AnalysisConfig());
        if (incremental.Graph == null)
        {
            Console.Error.WriteLine($"      Incremental returned mode={incremental.Mode}, reason={incremental.Reason}");
            return 1;
        }
        var incSymbols = incremental.Graph.Symbols.Count;
        var incEdges = incremental.Graph.Edges.Count;
        Console.WriteLine($"      Symbols: {incSymbols,8}  Edges: {incEdges,8}  Mode: {incremental.Mode}  ChangedFiles: {incremental.ChangedFileCount}");

        Console.WriteLine();
        if (fullSymbols == incSymbols && fullEdges == incEdges)
        {
            Console.WriteLine("VERIFIED — full and incremental produce identical graphs (INV-INCREMENTAL-XREF-001).");
            return 0;
        }
        Console.Error.WriteLine($"DRIFT DETECTED — symbols Δ={incSymbols - fullSymbols}, edges Δ={incEdges - fullEdges}.");
        Console.Error.WriteLine("This indicates LB-BUG-020 or a regression. File a bug.");
        return 1;
    }

    /// <summary>
    /// Parses a single named flag with a value: <c>--flag value</c>. Returns
    /// the value if found, null otherwise. For boolean flags (no value),
    /// use <see cref="HasFlag"/>.
    /// </summary>
    static string? ParseFlagValue(string[] args, string flagName)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flagName) return args[i + 1];
        return null;
    }

    /// <summary>
    /// Checks whether a value-less flag is present in the argument list.
    /// Returns true if <paramref name="flagName"/> appears anywhere in
    /// <paramref name="args"/>.
    /// </summary>
    static bool HasFlag(string[] args, string flagName)
    {
        foreach (var a in args)
            if (a == flagName) return true;
        return false;
    }

    static GraphSource BuildGraph(string? projectRoot, string? graphPath)
    {
        if (graphPath != null)
        {
            if (!Fs.FileExists(graphPath))
            {
                Console.Error.WriteLine($"Not found: {graphPath}");
                return new GraphSource();
            }
            using var stream = Fs.OpenRead(graphPath);
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
            var adapter = new RoslynWorkspaceAnalyzer(Fs);
            var result = new AnalyzeWorkspaceUseCase(adapter, Progress, UsageProbe)
                .Execute(projectRoot, new AnalysisConfig());
            LastUsage = result.Usage;
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
        Console.WriteLine("  lifeblood analyze --project <path> --rules hexagonal        Analyze + built-in rules");
        Console.WriteLine("  lifeblood analyze --project <path> --rules <rules.json>    Analyze + custom rules");
        Console.WriteLine("  lifeblood context --project <path>                         Generate AI context pack (JSON)");
        Console.WriteLine("  lifeblood context --project <path> --format md             Generate instruction file (markdown)");
        Console.WriteLine("  lifeblood export  --project <path>                         Export graph as JSON (stdout)");
        Console.WriteLine("  lifeblood export  --project <path> --out <file>            Export graph as JSON to file");
        Console.WriteLine("  lifeblood verify  --incremental --project <path>           Full vs incremental edge-count drift check (INV-INCREMENTAL-XREF-001)");
        Console.WriteLine();
        Console.WriteLine($"Built-in rule packs: {string.Join(", ", Analysis.RulePacks.BuiltIn)}");
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
