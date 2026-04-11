namespace Lifeblood.Server.Mcp;

/// <summary>
/// Declares all MCP tools with their schemas.
/// Separate from dispatch logic for clarity.
/// </summary>
public static class ToolRegistry
{
  /// <summary>
  /// Returns the wire-format tool list for the MCP <c>tools/list</c>
  /// response. When <paramref name="hasCompilationState"/> is false,
  /// write-side tool descriptions are prefixed with
  /// "[Unavailable. Load a project with lifeblood_analyze first]" so
  /// agents know not to call them before loading a project. The returned
  /// array is a pure <see cref="McpToolInfo"/> wire DTO — no availability
  /// metadata leaks onto the wire, and System.Text.Json serializes it
  /// with no required/init interaction quirks.
  ///
  /// <para>
  /// INV-TOOLREG-001: classification is by the typed
  /// <see cref="ToolDefinition.Availability"/> property on the internal
  /// registry record, never by tool name prefix. Adding a new tool
  /// without setting Availability is a compile error because the
  /// property is <c>required</c>.
  /// </para>
  /// </summary>
  public static McpToolInfo[] GetTools(bool hasCompilationState = true)
  {
  var definitions = GetDefinitions();
  var wire = new McpToolInfo[definitions.Length];
  for (var i = 0; i < definitions.Length; i++)
  {
  var def = definitions[i];
  var description = def.Description;
  if (!hasCompilationState && def.Availability == ToolAvailability.WriteSide)
  {
  description = "[Unavailable. Load a project with lifeblood_analyze first] " + description;
  }
  wire[i] = new McpToolInfo
  {
  Name = def.Name,
  Description = description,
  InputSchema = def.InputSchema,
  };
  }
  return wire;
  }

  /// <summary>
  /// Returns the internal tool definitions with their full classification
  /// metadata. This is the test seam for INV-TOOLREG-001 availability
  /// checks — tests that want to inspect <see cref="ToolDefinition.Availability"/>
  /// must consume this method, never <see cref="GetTools"/>, because
  /// availability is deliberately stripped at the wire boundary.
  /// </summary>
  public static ToolDefinition[] GetDefinitions() => GetAllTools();

  private static ToolDefinition[] GetAllTools() => new ToolDefinition[]
  {
  new()
  {
  Name = "lifeblood_analyze",
  Availability = ToolAvailability.ReadSide,
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
  readOnly = new { type = "boolean", description = "When true, uses streaming compilation (much lower memory. ~4GB vs ~7GB for large projects). Write-side tools (execute, find-references, rename, etc.) will be unavailable. Use for large projects when you only need read-side tools. Default: false." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_context",
  Availability = ToolAvailability.ReadSide,
  Description = "Generate an AI context pack from the loaded graph. Returns high-value files, boundaries, reading order, hotspots, dependency matrix.",
  InputSchema = new { type = "object", properties = new { } },
  },
  new()
  {
  Name = "lifeblood_lookup",
  Availability = ToolAvailability.ReadSide,
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
  Availability = ToolAvailability.ReadSide,
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
  Availability = ToolAvailability.ReadSide,
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
  Availability = ToolAvailability.ReadSide,
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
  Availability = ToolAvailability.ReadSide,
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
  //
  // Visual grouping only. The authoritative classification is the
  // Availability property on each record, not this comment.

  new()
  {
  Name = "lifeblood_execute",
  Availability = ToolAvailability.WriteSide,
  Description = "Execute C# code against the loaded workspace. Code runs in-process (trusted local sandbox. Blocklist + AST security checks, not process-isolated). Returns output, errors, and return value. Requires prior lifeblood_analyze with projectPath.",
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
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
  Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute the code. Auto-refreshes the workspace if any tracked file has been edited since the last analyze (opt out via `staleRefresh:false`).",
  InputSchema = new
  {
  type = "object",
  required = new[] { "code" },
  properties = new
  {
  code = new { type = "string", description = "C# code to compile-check" },
  moduleName = new { type = "string", description = "Module context for type resolution" },
  staleRefresh = new { type = "boolean", description = "If true (default), incrementally re-analyze the workspace before compile_check when any tracked file has changed on disk since the last analyze. Set false to check against the pinned workspace state." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_resolve_short_name",
  // ReadSide: consults SemanticGraph.GraphIndexes.FindByShortName (the
  // graph-level short-name bucket), not the Roslyn compilation host.
  // Resolves the long-standing classification ambiguity where this
  // tool sat under the write-side comment divider but none of the
  // prefix guards matched its name.
  Availability = ToolAvailability.ReadSide,
  Description = "Resolve a bare short name (e.g., 'MidiLearnManager') to its canonical symbol ID(s). Returns every matching symbol with its canonical id, file path, and kind. Use `mode` to control matching: 'exact' (default) is literal, 'contains' is substring, 'fuzzy' is a ranked near-match score. Zero-result responses automatically include ranked suggestions so you never hit a dead end.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "name" },
  properties = new
  {
  name = new { type = "string", description = "Short symbol name (no namespace)" },
  mode = new
  {
  type = "string",
  description = "Matching mode: 'exact' (default, literal), 'contains' (substring), or 'fuzzy' (ranked near-match).",
  @enum = new[] { "exact", "contains", "fuzzy" },
  },
  },
  },
  },
  new()
  {
  Name = "lifeblood_dead_code",
  Availability = ToolAvailability.ReadSide,
  Description = "Scan the loaded graph for symbols with no incoming semantic references — dead code candidates. Defaults: excludes public-visibility symbols (assumed reachable from outside) and test files. Returns canonical ids, kinds, file:line locations, and a short reason per hit. Use `includeKinds` to narrow (e.g. ['Method']). Phase 6 / DAWG R1.",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  includeKinds = new
  {
  type = "array",
  items = new { type = "string" },
  description = "Optional symbol-kind filter (e.g. ['Method','Type']). Case-insensitive. Unknown kinds are silently ignored. Default: Method, Type, Property, Field.",
  },
  excludePublic = new { type = "boolean", description = "Skip public symbols (default true)." },
  excludeTests = new { type = "boolean", description = "Skip files matching test conventions — any 'tests/' path segment or *Tests.cs / *Test.cs filename (default true)." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_partial_view",
  Availability = ToolAvailability.ReadSide,
  Description = "Return the combined source of every partial declaration of a type. Takes a type symbol id, walks the incoming Contains edges from File symbols to discover every partial file, reads each file via IFileSystem, and emits both per-segment source and a concatenated combined view with file headers. Phase 6 / DAWG R2.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "symbolId" },
  properties = new
  {
  symbolId = new { type = "string", description = "Canonical symbol id of the type (e.g. 'type:MyApp.MyClass')." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_invariant_check",
  Availability = ToolAvailability.ReadSide,
  Description = "Query the architectural invariants declared in the loaded project's CLAUDE.md. Three modes: (1) pass 'id' to fetch one invariant's full body, title, category, and source line; (2) pass mode='audit' (default) for a summary — total count, per-category breakdown, duplicate-id collisions, and parse warnings; (3) pass mode='list' for an id/title index across every declared invariant. The tool parses CLAUDE.md at the loaded project root, so lifeblood_analyze must have been called first to establish that root. Phase 8.",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  id = new { type = "string", description = "Exact invariant id (e.g. 'INV-CANONICAL-001'). Mutually exclusive with 'mode'." },
  mode = new { type = "string", description = "'audit' (default) or 'list'. Mutually exclusive with 'id'." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_search",
  Availability = ToolAvailability.ReadSide,
  Description = "Ranked keyword search across symbol names, qualified names, and persisted xml-documentation summaries. Use when you need to find a symbol by WHAT IT DOES, not by what it's NAMED — e.g., search 'canonicalize' and get back every symbol whose xmldoc mentions canonicalization even when none of them are literally called 'Canonicalize'. Returns ranked matches with canonical ids, file paths, lines, scores, and short context snippets. Distinct from lifeblood_resolve_short_name (which only searches the short-name index): this tool also mines the xmldoc corpus.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "query" },
  properties = new
  {
  query = new { type = "string", description = "Query text. Matched against symbol names, qualified names, and xmldoc summaries." },
  kinds = new
  {
  type = "array",
  items = new { type = "string" },
  description = "Optional symbol-kind filter (e.g. ['Method','Type']). Case-insensitive. Unknown kinds are silently ignored.",
  },
  limit = new { type = "integer", description = "Maximum number of results (default 20)." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_find_references",
  Availability = ToolAvailability.WriteSide,
  Description = "Find all references to a symbol across the loaded workspace. Returns file paths, line numbers, and span text. Set includeDeclarations=true to also return the symbol's declaration sites (one entry per partial declaration for partial types).",
  InputSchema = new
  {
  type = "object",
  required = new[] { "symbolId" },
  properties = new
  {
  symbolId = new { type = "string", description = "Symbol ID (e.g., type:MyApp.AuthService)" },
  includeDeclarations = new { type = "boolean", description = "When true, include the symbol's declaration sites in the result. Default false." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_find_definition",
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
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
  Availability = ToolAvailability.WriteSide,
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
