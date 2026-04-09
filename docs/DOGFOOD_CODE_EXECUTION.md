# Dogfood: Code Execution (2026-04-08)

Third dogfood milestone. Lifeblood's write-side MCP tools (6 tools, shipped in v0.3.0) were tested against Lifeblood itself. The MCP server loaded the Lifeblood solution, then each tool was exercised on Lifeblood's own types, symbols, and code.

**Setup:** MCP server over stdio (JSON-RPC 2.0). `lifeblood_analyze` loads the Lifeblood project at `D:\Projekti\Lifeblood`, building Roslyn compilations for all 11 modules. Write-side tools operate against those retained compilations. Session 2 expanded to 194 tests, 16 architecture rules, `required` Evidence fields.

## Test Matrix

30 integration tests covering all 12 MCP tools (6 read + 6 write) + edge cases. All pass.

### Read-Side Tools (6)

| Tool | Test | Result |
|------|------|--------|
| `lifeblood_analyze` | Load Lifeblood self | 797 symbols, 1971 edges, 10 modules, 0 violations |
| `lifeblood_context` | Generate context pack | Full pack: summary, high-value files, boundaries, hotspots, dependency matrix |
| `lifeblood_lookup` | Look up SemanticGraph | Returns type metadata, file, line, visibility, properties |
| `lifeblood_dependencies` | SemanticGraph outgoing | Edge, EdgeKind, Symbol, SymbolKind, etc. |
| `lifeblood_dependants` | SemanticGraph incoming | 157 affected at depth 3 (test files, analyzers, use cases) |
| `lifeblood_blast_radius` | SemanticGraph, depth 3 | 157 transitively affected symbols |

### Write-Side Tools (6)

| Tool | Test | Result |
|------|------|--------|
| `lifeblood_execute` | `return 42;` | success, returnValue=42, 304ms |
| `lifeblood_execute` | Access project types | `typeof(SemanticGraph).GetProperties().Length` returns 2 |
| `lifeblood_execute` | Access Roslyn types | `typeof(SyntaxTree).Assembly.GetName().Version` returns 4.12.0.0 |
| `lifeblood_diagnose` | All modules | 1143 diagnostics (mostly CS0246 from NuGet types not fully resolved in test/server modules) |
| `lifeblood_compile_check` | Valid snippet | success=true, zero diagnostics |
| `lifeblood_find_references` | `Symbol` type | 14 reference locations across GraphBuilder, extractors, tests |
| `lifeblood_rename` | SemanticGraph -> CodeGraph | 4 text edits across Domain files (preview only, not applied) |
| `lifeblood_format` | Messy code | `namespace X{class Y{int Z=>1;}}` -> formatted with proper spacing |

### Edge Cases (16)

| Test | Expected | Actual |
|------|----------|--------|
| Lookup nonexistent symbol | isError: "Symbol not found" | PASS |
| Dependencies missing symbolId | isError: "symbolId is required" | PASS |
| Execute syntax error | success=false, CS1040 | PASS |
| Execute File.Delete (blocked) | success=false, "Blocked pattern" | PASS |
| Execute Process.Start (blocked) | success=false, "Blocked pattern" | PASS |
| Compile-check syntax error | success=false, CS1001 | PASS |
| Compile-check invalid module | "Module not found. Available: ..." | PASS |
| Find references nonexistent | count=0 | PASS |
| Rename nonexistent symbol | editCount=0 | PASS |
| Rename missing newName | isError: "newName is required" | PASS |
| Execute hard timeout (200ms, Sleep 3000ms) | success=false, "timed out" | PASS |
| Blast radius nonexistent | affectedCount=0 | PASS |
| Analyze bad path | "Project directory not found" | PASS |
| Format empty code | isError: "code is required" | PASS |
| Session preserved after bad analyze | return 99 succeeds | PASS |
| Unknown tool | isError: "Unknown tool" | PASS |

## Bugs Found and Fixed

### B1: Notification Null Leak (Program.cs)

**Severity:** Protocol — corrupts stdio stream

`Dispatch()` returns `null!` for `initialized` notification (no ID = no response). But `Program.cs` still serialized and wrote `null` to stdout. Any JSON-RPC client that parses strictly would choke.

**Fix:** Added null check before serializing: `if (response == null) continue;`

### B2: Native DLL Metadata Error (RoslynWorkspaceAnalyzer.cs)

**Severity:** Critical — all write-side tools broken

`LoadBclReferences()` loaded ALL `.dll` files from the .NET runtime directory, including native DLLs (`System.IO.Compression.Native.dll`, `Microsoft.DiaSymReader.Native.amd64.dll`). These aren't valid .NET metadata, causing CS0009 errors in every compilation.

**Fix:** Added `IsNativeDll()` filter using `PEReader.HasMetadata` to skip non-.NET DLLs.

### B3: Session Corruption on Failed Analyze (GraphSession.cs)

**Severity:** High — one bad call breaks the session

`Load()` cleared write-side state (`CompilationHost = null`, etc.) upfront, before validation. If the new load failed (e.g., bad path), the previous session was destroyed.

**Fix:** Build new state into local variables, commit atomically only after full validation + analysis succeeds.

### B4: Symbol Resolution from Wrong Compilation (RoslynCompilationHost.cs, RoslynWorkspaceRefactoring.cs)

**Severity:** Critical — find_references returned 0, rename threw exception

`ResolveSymbol()` resolved symbols from standalone `_compilations`, but `SymbolFinder.FindReferencesAsync()` and `Renamer.RenameSymbolAsync()` need symbols that belong to the AdhocWorkspace's Solution. Different Roslyn compilation instances produce different symbol identity.

**Fix:** Resolve symbols from workspace project compilations (`project.GetCompilationAsync()`) instead of standalone compilations.

### B5: Missing NuGet Package Resolution (RoslynWorkspaceAnalyzer.cs)

**Severity:** Moderate — 1569 diagnostics from unresolved types

Compilations only had BCL references and cross-module CompilationReferences, but no NuGet package assemblies. Types from packages like `Microsoft.CodeAnalysis.CSharp` were unresolved.

**Fix:** Added `ResolveNuGetReferences()` that reads `obj/project.assets.json` (generated by `dotnet restore`) to find package DLLs in the NuGet global cache. Diagnostics dropped from 1569 to 1143 (remaining are from test/server modules with transitive NuGet deps).

### B6: CompileCheck False Negatives (RoslynCompilationHost.cs)

**Severity:** Moderate — valid code reported as failing

`CompileCheck` used `emitResult.Success` which includes pre-existing compilation errors from the target module. Valid user snippets were reported as failing because of unrelated CS0246 errors in the compilation.

**Fix:** Collect pre-existing diagnostic signatures before adding the snippet, then filter to only report NEW diagnostics. Success is based on whether the snippet introduced new errors, not whether the full compilation is clean.

### B7: Timeout Bypass (RoslynCodeExecutor.cs)

**Severity:** Security — synchronous blocking escapes timeout

`CancellationToken` passed to `CSharpScript.RunAsync` is only checked at compilation/evaluation boundaries. `Thread.Sleep()`, `while(true){}`, or other synchronous blocking operations ran to completion regardless of timeout.

**Fix:** Wrap script execution in `Task.Run()` + `Task.Wait(timeoutMs)` for hard thread-level timeout enforcement.

### W1: RS1024 Warning — SymbolEqualityComparer

**Severity:** Warning

`RoslynCompilationHost.cs` and `RoslynWorkspaceRefactoring.cs` used `!=` to compare Roslyn `ISymbol` instances instead of `SymbolEqualityComparer.Default.Equals()`.

**Fix:** Replaced all symbol comparisons with `SymbolEqualityComparer.Default.Equals()`.

## Hardening Pass (2026-04-08, post v0.2.0)

7-phase deep audit. All 7 phases clean. 3 additional bugs found and fixed, plus 3 preventive fixes.

### B8: Property Symbol ID Collision (SymbolIds.cs)

**Severity:** High — FindReferences and Rename silently failed for properties

`SymbolIds.Property()` generated `field:` prefix, making property IDs indistinguishable from field IDs. `FindInCompilation` looked for `IFieldSymbol` when kind="field", missing `IPropertySymbol` entirely. Properties silently returned 0 references and 0 rename edits.

**Fix:** Separate `property:` prefix in `SymbolIds.Property()`. Added `IPropertySymbol` handler in `FindInCompilation`. The `property:` check in `RoslynWorkspaceRefactoring` was dead code (unreachable because properties used `field:` prefix).

### B9: GetDiagnostics Silent Fallback (RoslynCompilationHost.cs)

**Severity:** Medium — misleading diagnostic results

When `GetDiagnostics("nonexistent_module")` was called with a module name that didn't exist, the ternary condition `moduleName != null && TryGetValue(...)` evaluated to `false`, falling through to scan ALL compilations. The caller got diagnostics from every module instead of empty.

**Fix:** Early return `Array.Empty<DiagnosticInfo>()` when `moduleName != null && !_compilations.ContainsKey(moduleName)`.

### B10: File.Exists Bypassing IFileSystem Port (RoslynWorkspaceAnalyzer.cs)

**Severity:** Medium — broke testability, violated hexagonal architecture

`ResolveNuGetReferences` used raw `File.Exists(dllPath)` instead of the injected `_fs.FileExists(dllPath)`. The `IFileSystem` port existed specifically to abstract filesystem access, but this call bypassed it.

**Fix:** Replaced with `_fs.FileExists(dllPath)`.

### Preventive Fixes

**P1: Blocked patterns expanded** (RoslynCodeExecutor.cs) — Added 13 patterns: `File.WriteAllText`, `File.WriteAllBytes`, `File.Move`, `File.Copy`, `Directory.CreateDirectory`, `Environment.SetEnvironmentVariable`, `Assembly.Load`, `Assembly.LoadFile`, `Assembly.LoadFrom`, `Assembly.UnsafeLoadFrom`, `Reflection.Emit`, `AssemblyBuilder`, `Thread.Abort`. Total: 5 → 18 patterns.

**P2: Property accessor dangling edges** (RoslynEdgeExtractor.cs) — `FindContainingMethodOrLocal` returned compiler-generated accessor methods (`get_X`/`set_X`) as edge sources, but the symbol extractor emits properties, not accessors. Changed to `continue` past `AccessorDeclarationSyntax` so edges source from the enclosing method instead.

**P3: NuGet catch-all narrowed** (RoslynWorkspaceAnalyzer.cs) — Bare `catch {}` in `ResolveNuGetReferences` could mask JSON schema changes in `project.assets.json`. Narrowed to `catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)`.

**P4: MCP parse error response** (Program.cs) — Malformed JSON-RPC input was logged to stderr but no response was sent. MCP client would hang. Added JSON-RPC 2.0 parse error response (code -32700). Also fixed `null!` return to proper nullable `JsonRpcResponse?`.

### Refactoring

**RoslynWorkspaceManager extracted** — `EnsureWorkspace()`, `ResolveSymbol()`, `FindInCompilation()`, and `ParseSymbolId()` were duplicated verbatim between `RoslynCompilationHost` and `RoslynWorkspaceRefactoring` (~80 LOC each). Extracted to shared `Internal/RoslynWorkspaceManager.cs`. Both consumers now use `Lazy<RoslynWorkspaceManager>`.

### Test Coverage

41 new tests (121 → 162):
- RoslynCompilationHost: 8 tests
- RoslynCodeExecutor: 11 tests (including all blocked patterns + timeout)
- RoslynWorkspaceRefactoring: 4 tests
- SymbolId parsing: 7 tests
- Architecture invariant (Analysis deps): 1 test

## Bugs Found and Fixed (Session 4 — 2026-04-09)

### B11: CS0518 "System.Object is not defined" on DAWG workspace (#1)

**Severity:** Critical — ALL `lifeblood_execute` calls fail on multi-module workspaces

**Reproduction:** Load DAWG (75 modules), then `lifeblood_execute` with `return 42;` → CS0518.

**Root cause (3 layers):**

1. `ScriptOptions.Default` has 25 references, all "Unresolved: System.Runtime" etc. They're named placeholders — no actual DLLs.
2. Previous code added `compilation.References` (target project's transitive deps), injecting Unity's netstandard BCL stubs.
3. Two competing `System.Object` definitions (host .NET 8 + Unity's netstandard) → CS0518.

**Fix:** Load host BCL explicitly from the running .NET runtime directory (`typeof(object).Assembly.Location` → runtime dir → 17 core DLLs). Use `WithReferences` to replace useless defaults. Only add CompilationReferences for project types — no transitive deps.

**Verification:** DAWG 75-module workspace: `return 42`, `Console.Write`, LINQ `Enumerable.Range(1,10).Sum()`, `typeof(object)`, string concat, generic collections — all pass.

**Tests:** 5 regression tests added:
- `CodeExecutor_WithDowngradedRefs_ResolvesSystemObject`
- `CodeExecutor_WithDowngradedRefs_CompilesCrossModuleCode`
- `CodeExecutor_WithDowngradedRefs_ConsoleOutputWorks`
- `CodeExecutor_WithDowngradedRefs_LinqWorks`
- `CodeExecutor_DowngradedRefs_HaveInMemoryDisplay`

**Bug class:** Any code path that adds target-project BCL references to CSharpScript options risks conflicting with the host runtime's BCL. Invariant: script execution must use ONLY the host runtime's BCL, never the target project's.

## Remaining Known Limitations

1. **Diagnose count (1143):** NuGet resolution from `project.assets.json` resolves direct packages but not all transitive dependencies. Modules that depend on many NuGet packages (Server.Mcp, Tests) still have unresolved types. This is a best-effort approach — full MSBuild resolution would require hosting MSBuild, which is intentionally avoided.

2. **Rename scope:** Rename edits are generated from the AdhocWorkspace, which only includes source files in the analyzed project. Renames don't propagate to external consumers.

## What This Proved

The write-side Roslyn tools transform Lifeblood from a read-only analysis framework into a bidirectional compiler-as-a-service. An AI agent can now:

1. Load a C# project and understand its architecture
2. Execute arbitrary C# code against the project's types
3. Check if code snippets compile in the project context
4. Find all references to any symbol
5. Preview rename operations across the workspace
6. Format code using Roslyn's formatter

All with safety guards (blocked patterns, hard timeouts, session isolation) and proper error handling.

**Lesson:** Testing your own MCP tools against your own codebase is the highest-value integration test. Every bug we found (7/7) was invisible to unit tests and would have affected real users on first connection.
