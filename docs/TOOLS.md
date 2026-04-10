# Tools

Lifeblood exposes **18 MCP tools** in one session. 8 read, 10 write. All share the same loaded workspace.

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match → truncated method form (single-overload lenient) → bare short name. Truncated ids and bare short names resolve correctly across the whole read surface.

## Write-side (compiler-as-a-service)

| Tool | What it does |
|------|-------------|
| **Execute** | Run C# code against your actual project types, in-process. Access any class, call any method, inspect any state. |
| **Diagnose** | Real compiler diagnostics. Errors, warnings, with file, line, and module. Not guesses. |
| **Compile-check** | "Will this snippet compile in my project?" answered in milliseconds. No domain reload. |
| **Find references** | Every caller, every consumer, across the entire workspace. Verified by the compiler. |
| **Find definition** | Go-to-definition. Resolves through interfaces, base classes, partials. Returns file, line, docs. |
| **Find implementations** | What types implement this interface? What methods override this? Semantic, not grep. |
| **Symbol at position** | Give a file:line:col, get the resolved symbol, type, and documentation. |
| **Documentation** | XML doc extraction. Pulls `<summary>`, `<param>`, `<returns>` from resolved symbols. |
| **Rename** | Safe rename across the workspace. Returns text edits as preview. The agent decides whether to apply. |
| **Format** | Roslyn's own formatter. Not regex hacks. |

## Read-side (semantic intelligence)

| Tool | What it does |
|------|-------------|
| **Analyze** | Load a project into a verified semantic graph. Symbols, edges, modules, violations. Pass `incremental: true` after the first analysis for fast re-analysis (only changed modules recompile, csproj edits trigger re-discovery). |
| **Context** | AI context pack with high-value files, boundaries, reading order, hotspots, dependency matrix. |
| **Lookup** | Symbol details: kind, file, line, visibility, properties. For partial types, returns the deterministic primary `filePath` and the full sorted `filePaths[]` of every partial declaration. |
| **Dependencies** | What does this symbol depend on? |
| **Dependants** | What depends on this symbol? |
| **Blast radius** | Change this symbol, what breaks? Transitive BFS over the dependency graph. |
| **File impact** | Change this file, what other files break? Derived from symbol-level edges. |
| **Resolve short name** | Discover canonical IDs from a bare short name when you don't know the namespace. Returns kind, file, line, and disambiguation candidates. |

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

## Symbol ID format

Tools that take a `symbolId` use this format:
- `type:Namespace.TypeName`
- `method:Namespace.TypeName.MethodName(ParamType)`
- `field:Namespace.TypeName.FieldName`
- `property:Namespace.TypeName.PropertyName`
- `mod:AssemblyName`
- `file:relative/path/to/File.cs`

Lifeblood owns the parameter-type display format for method IDs via `Internal.CanonicalSymbolFormat`. Every method-ID builder in the C# adapter routes through it — the symbol ID grammar does not silently drift with Roslyn version changes.

If you don't know the canonical id, ask `lifeblood_resolve_short_name MyType` and use the returned `symbolId`. The resolver also accepts truncated method ids like `method:Namespace.TypeName.MethodName` when there is exactly one matching overload.

## Incremental Re-Analyze

After the first `lifeblood_analyze`, subsequent calls with `incremental: true` only recompile modules whose source files changed since the last analysis. File changes are detected via filesystem timestamps.

```
lifeblood_analyze projectPath="/my/project"                    → full analysis (~60s)
lifeblood_analyze projectPath="/my/project" incremental=true   → seconds
```

Module additions/removals automatically fall back to full re-analyze.
