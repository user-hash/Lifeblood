namespace Lifeblood.Server.Mcp;

/// <summary>
/// Declares all MCP tools with their schemas.
/// Separate from dispatch logic for clarity.
/// </summary>
public static class ToolRegistry
{
    public static McpToolInfo[] GetTools() => new McpToolInfo[]
    {
        new()
        {
            Name = "lifeblood_analyze",
            Description = "Analyze a C# project or JSON graph file. Returns symbol/edge/module counts and violations. Loads the graph into memory for subsequent query tools.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    projectPath = new { type = "string", description = "Path to C# project root (with .sln or .csproj)" },
                    graphPath = new { type = "string", description = "Path to a graph.json file (alternative to projectPath)" },
                    rulesPath = new { type = "string", description = "Optional: path to a rules.json file for architecture validation" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_context",
            Description = "Generate an AI context pack from the loaded graph. Returns high-value files, boundaries, reading order, hotspots, dependency matrix.",
            InputSchema = new { type = "object", properties = new { } },
        },
        new()
        {
            Name = "lifeblood_lookup",
            Description = "Look up a symbol by ID. Returns the symbol's name, kind, file, line, visibility, and properties.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Symbol ID (e.g., type:MyApp.AuthService)" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_dependencies",
            Description = "Get all symbols that the given symbol depends on (outgoing non-Contains edges).",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Symbol ID" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_dependants",
            Description = "Get all symbols that depend on the given symbol (incoming non-Contains edges).",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Symbol ID" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_blast_radius",
            Description = "Compute what breaks if a symbol is changed. Transitive BFS over incoming dependency edges.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Symbol ID" },
                    maxDepth = new { type = "integer", description = "Maximum traversal depth (default: 10)" },
                },
            },
        },
    };
}
