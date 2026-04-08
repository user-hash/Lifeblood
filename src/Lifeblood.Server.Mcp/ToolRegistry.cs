namespace Lifeblood.Server.Mcp;

/// <summary>
/// Declares all MCP tools with their schemas.
/// Separate from dispatch logic for clarity.
/// </summary>
public static class ToolRegistry
{
    /// <summary>
    /// Returns all tools. When hasCompilationState is false, write-side tools are
    /// annotated as unavailable so agents know not to call them before loading a project.
    /// </summary>
    public static McpToolInfo[] GetTools(bool hasCompilationState = true)
    {
        var tools = GetAllTools();
        if (!hasCompilationState)
        {
            foreach (var tool in tools)
            {
                if (tool.Name.StartsWith("lifeblood_execute") ||
                    tool.Name.StartsWith("lifeblood_diagnose") ||
                    tool.Name.StartsWith("lifeblood_compile_check") ||
                    tool.Name.StartsWith("lifeblood_find_") ||
                    tool.Name.StartsWith("lifeblood_symbol_") ||
                    tool.Name.StartsWith("lifeblood_documentation") ||
                    tool.Name.StartsWith("lifeblood_rename") ||
                    tool.Name.StartsWith("lifeblood_format"))
                {
                    tool.Description = "[Unavailable — load a project with lifeblood_analyze first] " + tool.Description;
                }
            }
        }
        return tools;
    }

    private static McpToolInfo[] GetAllTools() => new McpToolInfo[]
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
                    rulesPath = new { type = "string", description = "Optional: built-in pack name (hexagonal, clean-architecture, lifeblood) or path to a rules.json file" },
                    incremental = new { type = "boolean", description = "When true, only recompiles modules with changed files since the last analysis. Much faster for iterative work. Falls back to full analysis if no previous analysis exists or if modules were added/removed. Default: false." },
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
        new()
        {
            Name = "lifeblood_file_impact",
            Description = "Get file-level impact: which files depend on this file and which files this file depends on. Derived from symbol-level edges. Answers 'if I change this file, what other files are affected?'",
            InputSchema = new
            {
                type = "object",
                required = new[] { "filePath" },
                properties = new
                {
                    filePath = new { type = "string", description = "Relative file path (e.g., src/MyApp/AuthService.cs)" },
                },
            },
        },

        // ── Write-side tools (require Roslyn compilation state) ──

        new()
        {
            Name = "lifeblood_execute",
            Description = "Execute C# code against the loaded workspace. Code runs in-process (trusted local sandbox — blocklist + AST security checks, not process-isolated). Returns output, errors, and return value. Requires prior lifeblood_analyze with projectPath.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "code" },
                properties = new
                {
                    code = new { type = "string", description = "C# code to compile and execute" },
                    imports = new { type = "array", items = new { type = "string" }, description = "Additional using namespaces" },
                    timeoutMs = new { type = "integer", description = "Execution timeout in milliseconds (default: 5000)" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_diagnose",
            Description = "Get compilation diagnostics (errors, warnings) for the loaded project. Optionally filter by module name.",
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    moduleName = new { type = "string", description = "Specific module to diagnose, or omit for all" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_compile_check",
            Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute the code.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "code" },
                properties = new
                {
                    code = new { type = "string", description = "C# code to compile-check" },
                    moduleName = new { type = "string", description = "Module context for type resolution" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_find_references",
            Description = "Find all references to a symbol across the loaded workspace. Returns file paths, line numbers, and span text.",
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
            Name = "lifeblood_find_definition",
            Description = "Find where a symbol is declared. Returns file path, line, column, display name, and documentation.",
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
            Name = "lifeblood_find_implementations",
            Description = "Find all types/methods that implement an interface or override a virtual member.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Interface, abstract class, or virtual method ID" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_symbol_at_position",
            Description = "Resolve what symbol is at a specific source position. Returns symbol ID, name, kind, qualified name, and documentation.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "filePath", "line", "column" },
                properties = new
                {
                    filePath = new { type = "string", description = "Source file path (absolute or relative)" },
                    line = new { type = "integer", description = "Line number (1-based)" },
                    column = new { type = "integer", description = "Column number (1-based)" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_documentation",
            Description = "Get XML documentation for a symbol. Returns the summary content.",
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
            Name = "lifeblood_rename",
            Description = "Rename a symbol across the workspace. Returns text edits (does NOT apply them). The caller decides whether to apply.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "symbolId", "newName" },
                properties = new
                {
                    symbolId = new { type = "string", description = "Symbol ID to rename" },
                    newName = new { type = "string", description = "The new name" },
                },
            },
        },
        new()
        {
            Name = "lifeblood_format",
            Description = "Format C# code using Roslyn's formatter. Returns the formatted code string.",
            InputSchema = new
            {
                type = "object",
                required = new[] { "code" },
                properties = new
                {
                    code = new { type = "string", description = "C# code to format" },
                },
            },
        },
    };
}
