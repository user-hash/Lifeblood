namespace Lifeblood.CLI;

class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("Lifeblood — Semantic Code Analysis");
        Console.WriteLine("https://github.com/user-hash/Lifeblood");
        Console.WriteLine();

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        // TODO: implement commands (analyze, graph, rules, report)
        Console.WriteLine($"Command: {args[0]}");
        Console.WriteLine("Not yet implemented. See README.md for project status.");
        return 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  lifeblood analyze --project <path> --rules <rules.json>");
        Console.WriteLine("  lifeblood analyze --graph <graph.json> --rules <rules.json>");
        Console.WriteLine("  lifeblood graph --project <path> --output <graph.json>");
        Console.WriteLine();
        Console.WriteLine("The --graph option accepts pre-built JSON from any language adapter.");
    }
}
