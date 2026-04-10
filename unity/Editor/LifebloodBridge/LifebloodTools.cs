using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;

namespace Lifeblood.UnityBridge
{
    /// <summary>
    /// All 18 Lifeblood semantic tools exposed as Unity MCP custom tools.
    /// Each class is auto-discovered by Unity MCP via [McpForUnityTool].
    /// Architecture: pure outer adapters. JObject in, JObject out, with all
    /// semantic work delegated to the Lifeblood sidecar process.
    /// </summary>

    // ═══════════════════════════════════════════════════════════════
    // Session management
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_analyze_project",
        Description = "Analyze the Unity project with Roslyn. Loads semantic graph (symbols, edges, types, dependencies) into the Lifeblood server. Call this once before using other lifeblood_ tools. Pass incremental=true after the first analysis for fast re-analyze (only recompiles changed files).",
        Group = "code-intelligence")]
    public static class LifebloodAnalyzeProject
    {
        [ToolParameter("When true, only recompile modules with changed files (much faster). Default: false.", Required = false)]
        public static string incremental;

        public static object HandleCommand(JObject @params)
        {
            var isIncremental = @params?["incremental"]?.ToString()?.ToLowerInvariant() == "true";
            return LifebloodBridgeClient.Instance.AnalyzeCurrentProject(isIncremental);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Read-side: Graph queries (6 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_context",
        Description = "Generate an AI context pack from the loaded graph. Returns high-value files, boundaries, reading order, hotspots, dependency matrix, invariants, and violations.",
        Group = "code-intelligence")]
    public static class LifebloodContext
    {
        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_context", @params);
        }
    }

    [McpForUnityTool("lifeblood_lookup",
        Description = "Look up a symbol by ID. Returns name, kind, file, line, visibility, properties.",
        Group = "code-intelligence")]
    public static class LifebloodLookup
    {
        [ToolParameter("Symbol ID (e.g. type:MyApp.AuthService or method:MyApp.AuthService.Login(string))")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_lookup", @params);
        }
    }

    [McpForUnityTool("lifeblood_dependencies",
        Description = "Get all symbols that the given symbol depends on (outgoing non-Contains edges).",
        Group = "code-intelligence")]
    public static class LifebloodDependencies
    {
        [ToolParameter("Symbol ID")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_dependencies", @params);
        }
    }

    [McpForUnityTool("lifeblood_dependants",
        Description = "Get all symbols that depend on the given symbol (incoming non-Contains edges). Shows who uses this symbol.",
        Group = "code-intelligence")]
    public static class LifebloodDependants
    {
        [ToolParameter("Symbol ID")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_dependants", @params);
        }
    }

    [McpForUnityTool("lifeblood_blast_radius",
        Description = "Compute what breaks if a symbol is changed. Transitive BFS over incoming dependency edges. Essential before refactoring.",
        Group = "code-intelligence")]
    public static class LifebloodBlastRadius
    {
        [ToolParameter("Symbol ID to analyze")]
        public static string symbolId;

        [ToolParameter("Maximum traversal depth (default: 10)", Required = false)]
        public static string maxDepth;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_blast_radius", @params);
        }
    }

    [McpForUnityTool("lifeblood_file_impact",
        Description = "Get file-level impact: which files depend on this file and which files this file depends on. Derived from symbol-level edges. Answers 'if I change this file, what other files are affected?'",
        Group = "code-intelligence")]
    public static class LifebloodFileImpact
    {
        [ToolParameter("Relative file path (e.g. Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.cs)")]
        public static string filePath;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_file_impact", @params);
        }
    }

    [McpForUnityTool("lifeblood_resolve_short_name",
        Description = "Resolve a bare short name (e.g. 'AdaptiveBeatGrid') to its canonical symbol ID(s). Returns every matching symbol with its canonical id, file path, and kind. Use this to discover the canonical id of a type when you only know its short name and not its namespace.",
        Group = "code-intelligence")]
    public static class LifebloodResolveShortName
    {
        [ToolParameter("Short symbol name (no namespace, e.g. 'MidiLearnManager')")]
        public static string name;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_resolve_short_name", @params);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Write-side: Compilation & diagnostics (4 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_diagnose",
        Description = "Get compilation diagnostics (errors, warnings) for the project. Optionally filter by module/assembly name.",
        Group = "code-intelligence")]
    public static class LifebloodDiagnose
    {
        [ToolParameter("Module name to filter (optional)", Required = false)]
        public static string moduleName;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_diagnose", @params);
        }
    }

    [McpForUnityTool("lifeblood_compile_check",
        Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute.",
        Group = "code-intelligence")]
    public static class LifebloodCompileCheck
    {
        [ToolParameter("C# code to compile-check")]
        public static string code;

        [ToolParameter("Module context for type resolution (optional)", Required = false)]
        public static string moduleName;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_compile_check", @params);
        }
    }

    [McpForUnityTool("lifeblood_find_references",
        Description = "Find all source locations that reference a symbol. Returns file paths, line numbers, and span text.",
        Group = "code-intelligence")]
    public static class LifebloodFindReferences
    {
        [ToolParameter("Symbol ID to search for")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_find_references", @params);
        }
    }

    [McpForUnityTool("lifeblood_find_definition",
        Description = "Find where a symbol is declared. Returns file path, line, column, display name, and XML documentation.",
        Group = "code-intelligence")]
    public static class LifebloodFindDefinition
    {
        [ToolParameter("Symbol ID to find definition for")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_find_definition", @params);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Write-side: Semantic operations (6 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_find_implementations",
        Description = "Find all types that implement an interface or override a virtual member. Returns symbol IDs of implementing types/methods.",
        Group = "code-intelligence")]
    public static class LifebloodFindImplementations
    {
        [ToolParameter("Interface, abstract class, or virtual method symbol ID")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_find_implementations", @params);
        }
    }

    [McpForUnityTool("lifeblood_symbol_at_position",
        Description = "Resolve what symbol is at a specific source position. Returns symbol ID, name, kind, qualified name, and documentation.",
        Group = "code-intelligence")]
    public static class LifebloodSymbolAtPosition
    {
        [ToolParameter("Source file path (absolute or relative)")]
        public static string filePath;

        [ToolParameter("Line number (1-based)")]
        public static string line;

        [ToolParameter("Column number (1-based)")]
        public static string column;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_symbol_at_position", @params);
        }
    }

    [McpForUnityTool("lifeblood_documentation",
        Description = "Get XML documentation summary for a symbol.",
        Group = "code-intelligence")]
    public static class LifebloodDocumentation
    {
        [ToolParameter("Symbol ID")]
        public static string symbolId;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_documentation", @params);
        }
    }

    [McpForUnityTool("lifeblood_rename",
        Description = "Preview a rename across the workspace. Returns text edits with file paths and line/column positions. Does NOT apply the edits — the caller decides.",
        Group = "code-intelligence")]
    public static class LifebloodRename
    {
        [ToolParameter("Symbol ID to rename")]
        public static string symbolId;

        [ToolParameter("The new name")]
        public static string newName;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_rename", @params);
        }
    }

    [McpForUnityTool("lifeblood_format",
        Description = "Format C# code using Roslyn's formatter. Returns the formatted code string.",
        Group = "code-intelligence")]
    public static class LifebloodFormat
    {
        [ToolParameter("C# code to format")]
        public static string code;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_format", @params);
        }
    }

    [McpForUnityTool("lifeblood_execute",
        Description = "Execute C# code against the loaded workspace. Runs in the Lifeblood sidecar process (not Unity). Has security scanner (blocklist + AST checks). Returns output, errors, and return value.",
        Group = "code-intelligence")]
    public static class LifebloodExecute
    {
        [ToolParameter("C# code to compile and execute")]
        public static string code;

        [ToolParameter("Additional using namespaces (JSON array)", Required = false)]
        public static string imports;

        [ToolParameter("Execution timeout in milliseconds (default: 5000)", Required = false)]
        public static string timeoutMs;

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallTool("lifeblood_execute", @params);
        }
    }
}
