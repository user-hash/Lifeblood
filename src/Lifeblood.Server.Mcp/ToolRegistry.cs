using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>
/// Declares all MCP tools with their schemas.
/// Separate from dispatch logic for clarity.
/// </summary>
public static class ToolRegistry
{
  // Envelope-classification presets. Single source of truth for the
  // truth-tier / confidence each tool ships under (INV-ENVELOPE-001).
  // Every read-side tool registration sets one of these on its
  // EnvelopeClassification property; the response decorator reads
  // them straight off ToolRegistry so registry and decorator cannot
  // drift. Adding a new tier is a one-line entry here.
  private static readonly EnvelopeClassification SemanticProven = new()
  {
    TruthTier = TruthTier.Semantic,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
  };

  private static readonly EnvelopeClassification DerivedProven = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Semantic",
  };

  private static readonly EnvelopeClassification DerivedInferred = new()
  {
    TruthTier = TruthTier.Derived,
    Confidence = ConfidenceBand.Proven,
    EvidenceSource = "Inferred",
  };

  private static readonly EnvelopeClassification HeuristicAdvisorySearch = new()
  {
    TruthTier = TruthTier.Heuristic,
    Confidence = ConfidenceBand.Advisory,
    EvidenceSource = "Heuristic",
  };

  private static readonly EnvelopeClassification HeuristicAdvisoryDeadCode = new()
  {
    TruthTier = TruthTier.Heuristic,
    Confidence = ConfidenceBand.Advisory,
    EvidenceSource = "Heuristic",
    Limitations = new[]
    {
      "Dead-code is reachability-only — runtime entry points (Program.Main), Unity reflection-based dispatch ([RuntimeInitializeOnLoadMethod], MonoBehaviour magic methods, UnityEvent YAML bindings) are not visible to static analysis and may surface as false positives.",
    },
  };

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
  EnvelopeClassification = SemanticProven,
  Description = "Analyze a C# project or JSON graph file. Returns symbol/edge/module counts and violations. Loads the graph into memory for subsequent query tools. Incremental responses report both `changedSourceFiles` (number of .cs files that re-extracted this round) and `touchedGraphFiles` (how many graph entries were rebuilt) — currently the same value, surface kept stable for future divergence. Unity note: a `.cs` file added to disk but not yet seen by the Unity editor (no `.meta` sibling) WILL be picked up by Lifeblood's incremental walker on the next analyze; if Unity later assigns the file a different GUID, the next full analyze refreshes all symbol IDs.",
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
  EnvelopeClassification = SemanticProven,
  Description = "Generate an AI context pack from the loaded graph. Returns summary, high-value files, boundaries, invariants, hotspots, reading order, and a module dependency matrix. Each section has a smart default cap so the response fits inside conservative tool-result budgets even on multi-module Unity workspaces (DAWG: 87 modules, 53k symbols). Override per-section caps with `maxFiles` / `maxBoundaries` / `maxHotspots` / `maxReadingOrder` / `maxMatrixEntries` (use -1 for unlimited or 0 to drop the section entirely). Pass `summarize:true` to drop every list-section to 0 and return only summary + invariants + violations — the smallest viable shape. Pass `sections:[\"summary\",\"boundaries\"]` to allow-list specific sections; everything not listed is replaced with an empty array. Every clipped section is reported in the response's `truncated` map with its full pre-clip count so callers know what was hidden. Closes LB-FR-022.",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  summarize = new { type = "boolean", description = "Smallest viable response: drop every list-section to 0, keep summary + invariants + violations. Defaults to false." },
  sections = new { type = "array", items = new { type = "string" }, description = "Optional allowlist of section names to include. Sections not on the list are emitted as empty arrays. Recognised: highValueFiles, boundaries, hotspots, readingOrder, dependencyMatrix. Summary, invariants, and violations are always retained." },
  maxFiles = new { type = "integer", description = "Cap on highValueFiles entries. Default 25. -1 unlimited; 0 drops the section." },
  maxBoundaries = new { type = "integer", description = "Cap on boundaries entries (one per module). Default 50." },
  maxHotspots = new { type = "integer", description = "Cap on hotspots entries. Default 20." },
  maxReadingOrder = new { type = "integer", description = "Cap on readingOrder entries. Default 50." },
  maxMatrixEntries = new { type = "integer", description = "Cap on dependencyMatrix entries (module-to-module edges). Default 100. The full matrix on a 87-module workspace is ~2600 entries." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_lookup",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = SemanticProven,
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
  EnvelopeClassification = SemanticProven,
  Description = "Get all symbols that the given symbol depends on (outgoing non-Contains edges). Note: outgoing edges are recorded at the symbol level where the reference physically appears — `Calls` edges live on the calling method, `References` edges live on the referencing field/property/method body, etc. A query for type-level outbound edges (`type:My.Service`) typically returns 0 because the type itself does not author calls; query its members (or use `lifeblood_blast_radius` to walk the transitive incoming closure) to see real coupling. Closes LB-OBSERVATION-001.",
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
  EnvelopeClassification = SemanticProven,
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
  EnvelopeClassification = DerivedProven,
  Description = "Compute what breaks if a symbol is changed. Transitive BFS over incoming dependency edges. Every response carries `directDependants` (the immediate one-hop count, distinct from the transitive total) so callers can distinguish a symbol with 5 direct callers from one with 5 transitive blast-radius members. Use `summarize:true` to get a compact result that does not embed the full affected-id array — useful when transitive blast on a popular type would otherwise return a multi-megabyte response. `maxResults` caps the embedded array regardless of summarize mode; the `truncated` flag tells callers whether the array was clipped.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "symbolId" },
  properties = new
  {
  symbolId = new { type = "string", description = "Symbol ID" },
  maxDepth = new { type = "integer", description = "Maximum traversal depth (default: 10)" },
  summarize = new { type = "boolean", description = "When true, omit the full affected-id array and return only counts + a small preview (size capped by maxResults). Defaults to false." },
  maxResults = new { type = "integer", description = "Maximum number of affected-symbol IDs embedded in the response. When the transitive set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_file_impact",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedInferred,
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
  Description = "Execute C# code against the loaded workspace. Code runs in-process (trusted local sandbox — blocklist + AST security checks, not process-isolated). Returns output, errors, and return value. Requires prior lifeblood_analyze with projectPath. The script's globals carry `Graph`, `Compilations`, `ModuleDependencies` plus the `Help` introspection string — see `RoslynSemanticView`. When the workspace is Unity-shaped (Library/ exists at the project root), Unity build artifacts under Library/ScriptAssemblies, Library/Bee/artifacts and Library/PackageCache are auto-injected as references so scripts can touch UnityEngine types; if no build artifacts are found a `runtimeAssemblyWarnings` entry tells the caller to run a Unity build first. Pass `targetProfile` to compile against a specific runtime ref-pack — `'host'` (default), `'net-standard-2.1'`, or `'net-6.0'`. Missing ref-packs surface a `targetRuntimeWarnings` entry instead of a hard failure.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "code" },
  properties = new
  {
  code = new { type = "string", description = "C# code to compile and execute" },
  imports = new { type = "array", items = new { type = "string" }, description = "Additional using namespaces" },
  timeoutMs = new { type = "integer", description = "Execution timeout in milliseconds (default: 5000)" },
  targetProfile = new
  {
  type = "string",
  description = "Target runtime profile for the script's BCL references. 'host' (default) uses the running .NET runtime; 'net-standard-2.1' / 'net-6.0' swap in the matching reference pack when installed locally. Unknown values fall back to 'host' with a targetRuntimeWarnings entry.",
  @enum = new[] { "host", "net-standard-2.1", "net-6.0" },
  },
  },
  },
  },
  new()
  {
  Name = "lifeblood_diagnose",
  Availability = ToolAvailability.WriteSide,
  Description = "Get compilation diagnostics (errors, warnings) for the loaded project. Without filters, returns the full project's diagnostics. Pass `filePath` (relative or absolute) to scope diagnostics to one source file — useful when you want to verify a single file you just edited without drowning in a 300k-line project dump. Pass `moduleName` to scope to a single module. `filePath` and `moduleName` may be combined (file scope wins; module is used to disambiguate which compilation contains the file when the same path appears in multiple modules).",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  filePath = new { type = "string", description = "Source file path (relative to the project root, or absolute). When set, diagnostics are scoped to this file's syntax tree." },
  moduleName = new { type = "string", description = "Specific module to diagnose, or omit for all. When combined with filePath, picks the module that contains the file." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_compile_check",
  Availability = ToolAvailability.WriteSide,
  Description = "Check if a C# code snippet compiles in the project context. Returns success/failure with diagnostics. Does not execute the code. Pass either `code` (inline source) or `filePath` (relative or absolute path; the file is read off disk) — exactly one is required. Auto-refreshes the workspace if any tracked file has been edited since the last analyze (opt out via `staleRefresh:false`).",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  code = new { type = "string", description = "C# code to compile-check. Mutually exclusive with filePath." },
  filePath = new { type = "string", description = "Path to a .cs file (relative to project root, or absolute) to read and compile-check. Mutually exclusive with code." },
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
  EnvelopeClassification = SemanticProven,
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
  EnvelopeClassification = HeuristicAdvisoryDeadCode,
  Description = "[EXPERIMENTAL — ADVISORY ONLY] Scan the loaded graph for symbols with no incoming semantic references — dead code CANDIDATES. Defaults: excludes public-visibility symbols (assumed reachable from outside) and test files. Returns canonical ids, kinds, file:line locations, and a short reason per hit. Use `includeKinds` to narrow (e.g. ['Method']). **Known false-positive classes (INV-DEADCODE-001):** (1) symbols referenced only via method-group conversion — methods passed as delegates to `Lazy<T>`, event handlers, LINQ chains; (2) methods whose call-site canonical id diverges from the definition-side id in full multi-module workspaces (pre-existing extraction gap under investigation for v0.6.4); (3) fields read via same-class instance access when the enclosing type has no external references. Treat every finding as a candidate to verify with `lifeblood_find_references` before acting, and remember that `find_references` has the same known gap class. Phase 6 / DAWG R1.",
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
  EnvelopeClassification = SemanticProven,
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
  EnvelopeClassification = SemanticProven,
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
  Name = "lifeblood_authority_report",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "Quantify how much architectural authority a type holds. Returns implementedInterfaceCount, ownedPublicSurface (public method/property/field/event count, nested types excluded), per-implemented-interface breakdown (member count + distinct consumers reaching it via Calls/References edges), and a forwarderRatio (PureForwarder methods / total methods, in [0.0,1.0]; -1.0 sentinel when classification data is missing). Use this to triage host/owner types: many interfaces + low ownedPublicSurface + high forwarderRatio = candidate for splitting; concentrated public surface + low forwarderRatio = doing real work.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "symbolId" },
  properties = new
  {
  symbolId = new { type = "string", description = "Canonical id of a type (e.g. 'type:My.Service')." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_port_health",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "Score how 'alive' the members of an interface or class are. Walks the type's Contains edges, runs an incoming-edge check on every member, and reports memberCount, liveMembers (>=1 incoming non-Contains edge), deadMembers, livenessPct, plus a verdict — 'healthy' (>=75% live), 'mixed', or 'vestigial' (<25% live). Use to spot ports/types that are mostly dead surface.",
  InputSchema = new
  {
  type = "object",
  required = new[] { "symbolId" },
  properties = new
  {
  symbolId = new { type = "string", description = "Canonical id of an interface or class type." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_cycles",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = DerivedProven,
  Description = "List all dependency cycles in the loaded graph. Each entry is a strongly-connected component of size >= 2 — symbol IDs in cycle order. Computed from the same Tarjan SCC pass that already runs in AnalysisPipeline; this tool just exposes the result without re-running analysis. Every response carries `count`, `totalSymbolCount`, `largestCycleSize`, and a `truncated` flag. Use `summarize:true` to get a compact result with `preview[]` instead of the full `cycles[]` — useful when a large workspace (DAWG: 117 SCCs ≈ 70KB) would otherwise overflow downstream tool-result limits. `maxResults` caps the embedded array regardless of mode.",
  InputSchema = new
  {
  type = "object",
  properties = new
  {
  summarize = new { type = "boolean", description = "When true, omit the full `cycles[]` array and return only counts + a small `preview[]` (size capped by maxResults). Defaults to false." },
  maxResults = new { type = "integer", description = "Maximum number of cycles embedded in the response. When the SCC set is larger, the array is clipped and `truncated:true` is set. Default: 500 in normal mode, 25 in summarize mode." },
  },
  },
  },
  new()
  {
  Name = "lifeblood_search",
  Availability = ToolAvailability.ReadSide,
  EnvelopeClassification = HeuristicAdvisorySearch,
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
