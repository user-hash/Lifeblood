using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;

namespace Lifeblood.UnityBridge
{
    /// <summary>
    /// All 19 Lifeblood semantic tools exposed as Unity MCP custom tools.
    /// Each class is auto-discovered by Unity MCP via [McpForUnityTool].
    /// Architecture: pure outer adapters. JObject in, JObject out, with all
    /// semantic work delegated to the workspace-shared Lifeblood host. Parameters
    /// live on nested <c>Parameters</c> properties because that is Coplay's
    /// reflection contract; every call uses its polling lifecycle because a
    /// cold semantic operation can exceed Unity MCP's synchronous deadline.
    /// </summary>

    // ═══════════════════════════════════════════════════════════════
    // Session management
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_analyze_project",
        Description = "Analyze the Unity project with Roslyn. Loads semantic graph (symbols, edges, types, dependencies) into the Lifeblood server. Call this once before using other lifeblood_ tools. Pass incremental=true after the first analysis for fast re-analyze (only recompiles changed files).",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodAnalyzeProject
    {
        public sealed class Parameters
        {
            [ToolParameter("When true, only recompile modules with changed files (much faster). Default: false.", Required = false, DefaultValue = "false")]
            public bool incremental { get; set; }

            [ToolParameter("When true, use streaming read-only analysis. Write-side tools require false. Default: false.", Required = false, DefaultValue = "false")]
            public bool readOnly { get; set; }

            [ToolParameter("Permit an incremental request to widen to full analysis when its cache cannot be reused. Default: false.", Required = false, DefaultValue = "false")]
            public bool allowFullFallback { get; set; }

            [ToolParameter("Optional Lifeblood define profiles, for example Editor and Player.", Required = false)]
            public string[] defineProfiles { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.AnalyzeCurrentProjectWithPolling(@params);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Read-side: Graph and operation-fact queries (8 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_context",
        Description = "Generate an AI context pack from the loaded graph. Returns high-value files, boundaries, reading order, hotspots, dependency matrix, invariants, and violations.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodContext
    {
        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_context", @params);
        }
    }

    [McpForUnityTool("lifeblood_lookup",
        Description = "Look up a symbol by ID. Returns name, kind, file, line, visibility, properties.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodLookup
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID (e.g. type:MyApp.AuthService or method:MyApp.AuthService.Login(string))")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_lookup", @params);
        }
    }

    [McpForUnityTool("lifeblood_dependencies",
        Description = "Get all symbols that the given symbol depends on (outgoing non-Contains edges).",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodDependencies
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_dependencies", @params);
        }
    }

    [McpForUnityTool("lifeblood_dependants",
        Description = "Get all symbols that depend on the given symbol (incoming non-Contains edges). Shows who uses this symbol.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodDependants
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_dependants", @params);
        }
    }

    [McpForUnityTool("lifeblood_blast_radius",
        Description = "Compute what breaks if a symbol is changed. Transitive BFS over incoming dependency edges. Essential before refactoring.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodBlastRadius
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID to analyze")]
            public string symbolId { get; set; }

            [ToolParameter("Maximum traversal depth (default: 10)", Required = false)]
            public int? maxDepth { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_blast_radius", @params);
        }
    }

    [McpForUnityTool("lifeblood_file_impact",
        Description = "Get file-level impact: which files depend on this file and which files this file depends on. Derived from symbol-level edges. Answers 'if I change this file, what other files are affected?'",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodFileImpact
    {
        public sealed class Parameters
        {
            [ToolParameter("Relative file path (e.g. Assets/_Project/Scripts/BeatGrid/AdaptiveBeatGrid.cs)")]
            public string filePath { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_file_impact", @params);
        }
    }

    [McpForUnityTool("lifeblood_resolve_short_name",
        Description = "Resolve a bare short name (e.g. 'AdaptiveBeatGrid') to its canonical symbol ID(s). Returns every matching symbol with its canonical id, file path, and kind. Use this to discover the canonical id of a type when you only know its short name and not its namespace.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodResolveShortName
    {
        public sealed class Parameters
        {
            [ToolParameter("Short symbol name (no namespace, e.g. 'MidiLearnManager')")]
            public string name { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_resolve_short_name", @params);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Write-side: Compilation & diagnostics (4 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_contract_audit",
        Description = "Evaluate a versioned consumer contract manifest over one bounded operation-fact scan. Supply manifestJson or a workspace-contained manifestPath. Summary-first by default.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodContractAudit
    {
        public sealed class Parameters
        {
            [ToolParameter("Inline contract manifest JSON. Supply exactly one of manifestJson or manifestPath.", Required = false)]
            public string manifestJson { get; set; }

            [ToolParameter("Contract manifest path inside the analyzed Unity workspace. Supply exactly one of manifestJson or manifestPath.", Required = false)]
            public string manifestPath { get; set; }

            [ToolParameter("Optional committed define profile.", Required = false)]
            public string profileScope { get; set; }

            [ToolParameter("Optional module/asmdef scope.", Required = false)]
            public string moduleScope { get; set; }

            [ToolParameter("Optional source-file allowlist.", Required = false)]
            public string[] filePaths { get; set; }

            [ToolParameter("Optional containing-symbol allowlist.", Required = false)]
            public string[] containingSymbolIds { get; set; }

            [ToolParameter("Optional rule-family or exact contract-id allowlist.", Required = false)]
            public string[] includeRuleIds { get; set; }

            [ToolParameter("Maximum emitted operation facts. Default 50000; hard cap 250000.", Required = false)]
            public int? maxFacts { get; set; }

            [ToolParameter("Maximum returned findings. Default 200; hard cap 1000.", Required = false)]
            public int? maxFindings { get; set; }

            [ToolParameter("Maximum evidence records per finding. Default 8; hard cap 32.", Required = false)]
            public int? maxEvidencePerFinding { get; set; }

            [ToolParameter("Summary-first response. Default true; pass false for bounded evidence detail.", Required = false, DefaultValue = "true")]
            public bool summarize { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var forwarded = @params == null ? new JObject() : (JObject)@params.DeepClone();
            var manifestJson = forwarded.Value<string>("manifestJson");
            forwarded.Remove("manifestJson");
            if (!string.IsNullOrWhiteSpace(manifestJson))
                forwarded["manifest"] = JObject.Parse(manifestJson);

            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_contract_audit", forwarded);
        }
    }

    [McpForUnityTool("lifeblood_diagnose",
        Description = "Get compilation diagnostics (errors, warnings) for the project. Optionally filter by module/assembly name.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodDiagnose
    {
        public sealed class Parameters
        {
            [ToolParameter("Module name to filter (optional)", Required = false)]
            public string moduleName { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_diagnose", @params);
        }
    }

    [McpForUnityTool("lifeblood_compile_check",
        Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodCompileCheck
    {
        public sealed class Parameters
        {
            [ToolParameter("C# code to compile-check")]
            public string code { get; set; }

            [ToolParameter("Module context for type resolution (optional)", Required = false)]
            public string moduleName { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_compile_check", @params);
        }
    }

    [McpForUnityTool("lifeblood_find_references",
        Description = "Find all source locations that reference a symbol. Returns file paths, line numbers, and span text.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodFindReferences
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID to search for")]
            public string symbolId { get; set; }

            [ToolParameter("Include declaration sites in the result. Default: false.", Required = false, DefaultValue = "false")]
            public bool includeDeclarations { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_find_references", @params);
        }
    }

    [McpForUnityTool("lifeblood_find_definition",
        Description = "Find where a symbol is declared. Returns file path, line, column, display name, and XML documentation.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodFindDefinition
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID to find definition for")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_find_definition", @params);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Write-side: Semantic operations (6 tools)
    // ═══════════════════════════════════════════════════════════════

    [McpForUnityTool("lifeblood_find_implementations",
        Description = "Find all types that implement an interface or override a virtual member. Returns symbol IDs of implementing types/methods.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodFindImplementations
    {
        public sealed class Parameters
        {
            [ToolParameter("Interface, abstract class, or virtual method symbol ID")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_find_implementations", @params);
        }
    }

    [McpForUnityTool("lifeblood_symbol_at_position",
        Description = "Resolve what symbol is at a specific source position. Returns symbol ID, name, kind, qualified name, and documentation.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodSymbolAtPosition
    {
        public sealed class Parameters
        {
            [ToolParameter("Source file path (absolute or relative)")]
            public string filePath { get; set; }

            [ToolParameter("Line number (1-based)")]
            public int line { get; set; }

            [ToolParameter("Column number (1-based)")]
            public int column { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_symbol_at_position", @params);
        }
    }

    [McpForUnityTool("lifeblood_documentation",
        Description = "Get XML documentation summary for a symbol.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodDocumentation
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID")]
            public string symbolId { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_documentation", @params);
        }
    }

    [McpForUnityTool("lifeblood_rename",
        Description = "Preview a rename across the workspace. Returns text edits with file paths and line/column positions. Does NOT apply the edits — the caller decides.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodRename
    {
        public sealed class Parameters
        {
            [ToolParameter("Symbol ID to rename")]
            public string symbolId { get; set; }

            [ToolParameter("The new name")]
            public string newName { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_rename", @params);
        }
    }

    [McpForUnityTool("lifeblood_format",
        Description = "Format C# code using Roslyn's formatter. Returns the formatted code string.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodFormat
    {
        public sealed class Parameters
        {
            [ToolParameter("C# code to format")]
            public string code { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_format", @params);
        }
    }

    [McpForUnityTool("lifeblood_execute",
        Description = "Execute C# code against the loaded workspace. Runs in the shared Lifeblood host (not Unity). Has security scanner (blocklist + AST checks). Returns output, errors, and return value.",
        Group = "code-intelligence", RequiresPolling = true, MaxPollSeconds = 360)]
    public static class LifebloodExecute
    {
        public sealed class Parameters
        {
            [ToolParameter("C# code to compile and execute")]
            public string code { get; set; }

            [ToolParameter("Additional using namespaces", Required = false)]
            public string[] imports { get; set; }

            [ToolParameter("Execution timeout in milliseconds (default: 5000)", Required = false)]
            public int? timeoutMs { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            return LifebloodBridgeClient.Instance.CallToolWithPolling("lifeblood_execute", @params);
        }
    }
}
