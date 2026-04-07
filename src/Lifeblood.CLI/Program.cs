namespace Lifeblood.CLI;

class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("Lifeblood — Compiler truth in, AI context out");
        Console.WriteLine("https://github.com/user-hash/Lifeblood");
        Console.WriteLine();

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        Console.WriteLine($"Command: {args[0]}");
        Console.WriteLine("Not yet implemented. See docs/MASTERPLAN.md for project status.");
        return 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  lifeblood analyze --project <path>              Analyze via Roslyn adapter");
        Console.WriteLine("  lifeblood analyze --graph <graph.json>          Analyze pre-built JSON graph");
        Console.WriteLine("  lifeblood context --project <path>              Generate AI context pack");
        Console.WriteLine("  lifeblood rules --project <path> --rules <json> Validate architecture rules");
        Console.WriteLine("  lifeblood export --project <path> --output <f>  Export semantic graph");
    }
}
