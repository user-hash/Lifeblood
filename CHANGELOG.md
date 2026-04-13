# Changelog

All notable changes to Lifeblood are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.6.4] - 2026-04-13

### Fixed. Dead-code false positives and call-graph completeness

Five extraction gaps closed in `RoslynEdgeExtractor`. Root-cause compilation fix in `ModuleCompilationBuilder`. Self-analysis: 150 to 10 dead-code findings (93% reduction). Edges: 5,777 to 8,223 (+42%). Tests: 539 to 557 (+18 new, 0 regressions). All call-graph tools benefit: `find_references`, `dependants`, `blast_radius`, `file_impact`, `dead_code`.

- **BUG-004 (interface dispatch).** Method-level `Implements` edges via `FindImplementationForInterfaceMember` + `AllInterfaces`. Dead-code analyzer checks outgoing `Implements` as proof of liveness. Fixes ~54% of false positives.
- **BUG-005 (member access granularity).** Symbol-level `References` edges for properties and fields via `EmitSymbolLevelEdge` shared helper. `ExtractReferenceEdge` restructured to handle `IFieldSymbol` (bare field reads) and `IMethodSymbol` (method-group references: `Lazy<T>(Load)`, `event += Handler`). Fixes ~20% of false positives.
- **BUG-006 (null-conditional property).** `MemberBindingExpressionSyntax` handler for `obj?.Property` patterns. Fixes ~15% of false positives.
- **Lambda context.** `FindContainingMethodOrLocal` skips lambda syntax nodes. Calls inside `.Select(x => Foo(x))` attribute to the enclosing named method.
- **Implicit global usings (LB-INBOX-007).** `ModuleInfo.ImplicitUsings` discovered from `<ImplicitUsings>enable</>` in the csproj. `ModuleCompilationBuilder.CreateCompilation` injects a synthetic global using tree with the 7 standard namespaces. Without this, Roslyn could not resolve `List<>`, `Dictionary<>`, `HashSet<>`, etc. and `GetSymbolInfo` returned null for 42% of invocations. Follows the INV-COMPFACT-001..003 pattern.
- **Reference assemblies.** `BclReferenceLoader` prefers SDK pack reference assemblies from `dotnet/packs/Microsoft.NETCore.App.Ref/` over runtime implementation assemblies.

Remaining 10 findings: runtime entry points (6), static field initializer method-groups (2), static field in accessor (1), internal constructor (1). All correct or known edge-case.

## [0.6.3] - 2026-04-11

One release covering the full 12-commit span from `v0.6.1` through Phase 8. Adds four new MCP tools (semantic search, dead code, partial view, invariant check), five new port interfaces, the wrong-namespace resolver fallback, `compile_check` auto-wrap for library modules, the MCP wire/internal DTO split that unblocks Claude Code reconnect, tokenized ranked-OR search, a Linux CI fix, and a release-wide documentation sweep. Ships `lifeblood_dead_code` as experimental / advisory with three documented false-positive classes. **Tools: 18 to 22 (+4). Ports: 17 to 22 (+5). Tests: 362 to 539.**

### Commits included since v0.6.1

- `898b125` feat(resolver, extraction): improvement-master plan phases 0-4
- `e42f51b` feat(search): `lifeblood_search` tool + xmldoc persistence (phase 5)
- `db09dfe` feat(analysis, compile-check): phases 6+7 dead_code, partial_view, break_kind, auto-refresh
- `96abd06` fix(review): self-review pass with live-drift ratchet + dedup / classification edge cases
- `334b47d` fix(mcp): wire / internal split + single source of truth; unblocks Claude Code reconnect
- `9cc9e50` fix(search): tokenized ranked OR so multi-word queries stop collapsing to zero
- `ea08264` test(mcp): close stdio-loop and search dispatch coverage gaps
- `a077e35` fix(ci): extract `CsprojPaths` shared helper; unblocks Linux build
- `24f607e` fix(write): `compile_check` auto-wraps statement snippets for library modules
- `afbc358` fix(resolver): wrong-namespace inputs resolve via trailing short-name segment (`INV-RESOLVER-005`)
- `26bb8bf` feat(invariants): `lifeblood_invariant_check` (Phase 8)
- `c31b44a` docs(plans): publish Phase 8 design spike under `docs/plans/`

### Added. Resolver, extraction, and view hardening (phases 0-4)

- **`ISymbolResolver` port in Application**, with `LifebloodSymbolResolver` as the reference implementation in `Connectors.Mcp`. Every read-side MCP tool that takes a `symbolId` routes through one resolver before any graph or workspace lookup. Resolution order: exact canonical match, then truncated method form (single-overload lenient), then bare short name. Partial-type unification is computed as a read model on the resolution result, not a graph schema change. See `INV-RESOLVER-001..004`.
- **`IUserInputCanonicalizer` port**, with `CSharpUserInputCanonicalizer` handling primitive-alias rewriting (`System.String` to `string`, `global::` stripping) at step 0 of the pipeline so every diagnostic, `Candidates[]` entry, and log line quotes the canonical form rather than the user's raw input.
- **`RoslynSemanticView`**: typed adapter-side read-only view exposing `Compilations`, `Graph`, and `ModuleDependencies` as a single shared reference. Constructed once per `GraphSession.Load`. `RoslynCodeExecutor` consumes it as the script-host globals object. See `INV-VIEW-001..003`.

### Added. `lifeblood_search` (phase 5)

- **New tool** backed by the new `ISemanticSearchProvider` port and `LifebloodSemanticSearchProvider` reference implementation. Ranks symbols by name match, qualified-name match, and persisted xmldoc summary match.
- **XML doc persistence**: during symbol extraction the C# adapter now attaches `<summary>` text to `Symbol.Properties["xmlDocSummary"]` so the search provider can rank on what a symbol does, not just what it is named.

### Added. Dead code, partial view, and compile-check auto-refresh (phases 6-7)

- **New tool `lifeblood_dead_code`** (ships experimental / advisory in v0.6.3; see "Known limitation" below), backed by `IDeadCodeAnalyzer` and `LifebloodDeadCodeAnalyzer`. Scans the graph for symbols with no incoming semantic references.
- **New tool `lifeblood_partial_view`**, backed by `IPartialViewBuilder` and `LifebloodPartialViewBuilder`. Returns the combined source of every partial declaration of a type with file headers between segments.
- **`lifeblood_compile_check` auto-refresh**: if any tracked file on disk has changed since the last analyze, the handler incrementally re-analyzes before compiling the snippet. Opt out with `staleRefresh: false`. The response carries `autoRefreshed: true` plus `changedFileCount: N` when a refresh actually ran.

### Fixed. MCP wire / internal DTO split (`334b47d`)

- **The bug.** `McpToolInfo` was a conflated wire + internal type with `[JsonIgnore] required init ToolAvailability Availability`. .NET 8 `System.Text.Json` has a latent bug where `[JsonIgnore]` is not honoured on `required init` properties during serialization metadata construction: the runtime threw `JsonException` on every `tools/list` response, which Claude Code interpreted as a dead server and refused to reconnect.
- **The fix.** Split `ToolDefinition` (internal registry record, carries `Availability`) from `McpToolInfo` (pure wire DTO, no `Availability` field at all). `ToolRegistry.GetDefinitions()` returns definitions for test seams; `ToolRegistry.GetTools()` projects to wire DTOs, applying `[Unavailable...]` description decoration based on the session's compilation state. The wire DTO has no `required` / `init` interaction quirks, so `System.Text.Json` serializes it cleanly.
- **Typed availability dispatch.** Removed the previous 8-prefix string match that silently misclassified `lifeblood_resolve_short_name`. Classification is now via the typed `ToolAvailability` enum on `ToolDefinition`; omitting `Availability` is a compile error. See `INV-TOOLREG-001`.
- **Single source of truth for MCP protocol constants.** Protocol version, JSON-RPC method names, and notification method names live exclusively in `Lifeblood.Connectors.Mcp.McpProtocolSpec`. The Unity bridge ships a standalone mirror at `unity/Editor/LifebloodBridge/McpProtocolConstants.cs` pinned byte-equal to `McpProtocolSpec` by a ratchet test. Fixes two latent Unity bridge bugs: empty `initialize.params` and legacy `initialized` notification alias usage. See `INV-MCP-003`.

### Fixed. Resolver wrong-namespace short-name fallback (`afbc358`, `INV-RESOLVER-005`)

- **Two dogfood reports landed the same failure class.** User typed `type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter` when the real symbol lives in `Audio.Tuning`. User typed `type:Nebulae.BeatGrid.Audio.Tuning.DspPolicy` when the real symbol lives in `Infrastructure.Audio.Synthesis.Recipes`. Both cases returned three unrelated long-named `MixerScreenAdapter` properties as "Did you mean" suggestions.
- **Two compounding bugs.** Rules 1-3 in `LifebloodSymbolResolver.Resolve` all failed because the input had a kind prefix and namespace dots, but rule 3's bare short-name lookup only fires when the input has neither prefix nor dots. And the fallback ranker scored the full canonical-shaped input string against every bare symbol name; Levenshtein closeness is `candidateLength - distance`, which biases toward long candidate names regardless of semantic similarity.
- **Architectural fix** in `LifebloodSymbolResolver`: one helper, one new rule, one ranker correction.
  - `ExtractLikelyShortName(string)`: pure function. Strips kind prefix, strips method parameter list, returns the final dot-separated segment. Single source of truth used by both Rule 4 and the suggestion ranker.
  - **Rule 4** (`ResolveOutcome.ShortNameFromQualifiedInput` and `AmbiguousShortNameFromQualifiedInput`). After rules 1-3 fail and the extracted short name is non-empty, look it up via `SemanticGraph.FindByShortName`. Single hit resolves with a diagnostic explaining the namespace correction. Multiple hits surface all candidates. Zero hits fall through to `NotFound` with an honest diagnostic.
  - `SuggestNearMatchesInternal` passes the query through `ExtractLikelyShortName` before scoring. Literal short-name index hits land at `ShortNameHitScore = 1000`, deliberately above any reachable `ScoreCandidate` value, so real short-name matches always sort above fuzzy-ranking accidents.
- Every one of the 10 read-side and write-side tools that route through `ISymbolResolver` inherits the fix automatically (`INV-RESOLVER-001`).
- Pinned by `ResolverShortNameFallbackTests` (24 tests including both exact dogfood cases reproduced in synthetic graphs with bias-inducing noise symbols).

### Fixed. `lifeblood_compile_check` auto-wraps bare statement snippets (`24f607e`)

- **The bug.** The tool was documented as "compile a snippet in the project context" but fed raw text to `CSharpSyntaxTree.ParseText` and pasted the result at the top level of the target module. Library modules (the overwhelming majority of csprojs) refused bare statement snippets like `var x = 1 + 1;` with CS8805 "Program using top-level statements must be an executable". Users had to manually wrap snippets in `class _Probe { void M() { ... } }` to test anything that was not already a complete compilation unit.
- **The fix.** New `Internal.SnippetWrapper` helper. If the parsed `CompilationUnitSyntax` contains any type, namespace, or delegate declaration at the top level, pass through unchanged. Otherwise wrap the statements in a synthetic `class _LifebloodCompileProbe { void _LifebloodCompileBody() { ... } }` on a single inserted line above the user's body. Using directives are preserved at the top level. `MapLineToUser` remaps diagnostic line numbers back to the user's original coordinates.
- `RoslynCompilationHost.CompileCheck` routes through `SnippetWrapper.Prepare` and applies `MapLineToUser` when projecting diagnostics.
- Pinned by `SnippetWrapperTests` (21 tests) plus 5 end-to-end `CompileCheck` integration tests in `WriteSideToolTests`.

### Fixed. `lifeblood_search` tokenized ranked-OR scoring (`9cc9e50`)

- **The bug.** The provider scored against `sym.Name.Contains(query, ...)` with the whole untokenized query string. Dogfood against DAWG: `"interpolate"` returned 5 hits, `"interpolate values"` returned 0. `"quantize"` returned 5 hits, `"quantize timing to grid"` returned 0. A tool billed as ranked keyword search that zeros out the moment you add specificity.
- **The fix.** The query is now an ordered list of tokens, not an opaque string. Split on whitespace, dedup case-insensitively, drop tokens below 3 chars to keep noise from saturating scores, fall back to the whole trimmed query as a single literal when every token is sub-threshold so "id" or "db" still work. Each surviving token is an independent scoring signal; scores OR-accumulate across fields and tokens. Per-field weights and the FQ vs. bare-name dedup semantics are preserved.
- Pinned by 6 new regression tests in `SemanticSearchTests`.

### Fixed. Linux CI path normalization drift (`a077e35`)

- **CI was red on every commit since `96abd06`.** `ArchitectureInvariantTests.CompositionRoot_CLI_UsesOnlyAllowedModules` and the `ServerMcp` variant failed on Linux because `Path.GetFileNameWithoutExtension` treats backslash as a literal filename character on non-Windows hosts. Csproj `ProjectReference Include` attributes are authored in MSBuild convention (`..\Lifeblood.Domain\Lifeblood.Domain.csproj`), so the test received an unsplittable string and the allowlist check failed against `..\Lifeblood.Domain\Lifeblood.Domain` instead of `Lifeblood.Domain`.
- **The fix.** Same class as commits `c9606b9` and `562dc6a` ("normalize path separators at every csproj / sln raw-path site") but for the ratchet test that was missed. Extracted `Internal.CsprojPaths` with `NormalizeSeparators` and `GetReferencedModuleName`, and routed every caller through it. `RoslynModuleDiscovery`'s four call sites and the architecture ratchet test now share one implementation, so future csproj-path bugs cannot affect one without affecting the other.

### Added. `lifeblood_invariant_check` (Phase 8, `26bb8bf`, `INV-INVARIANT-001`)

- **New MCP tool** that turns `CLAUDE.md`'s 58+ architectural invariants into queryable structured data. Three modes selected by parameter shape:
  - `id`: return one invariant's full body, title, category, and source line.
  - `mode: "audit"` (default): summary with total count, per-category breakdown, duplicate-id collisions, and parse warnings.
  - `mode: "list"`: every id plus title plus category plus source line, bodies omitted for compact responses.
- **Hexagonal stack**, five new classes across Application and `Connectors.Mcp`:
  - `Invariant`, `InvariantAudit`, `CategoryCount`, `DuplicateInvariantId` value types in `Lifeblood.Application.Ports.Right.Invariants/`.
  - `IInvariantProvider` port.
  - `Internal.ClaudeMdInvariantParser`: pure text to records. Handles shape A (`- **INV-X-N**: body`) and shape B (`- **INV-X-N. Title.** body`). Multi-line titles, multi-paragraph bodies, duplicate detection, category inference from id prefix including multi-segment prefixes (`INV-USAGE-PROBE-002` to `USAGE-PROBE`), backtick-code-span-safe first-sentence extraction.
  - `Internal.InvariantParseCache<T>`: generic timestamp-invalidated cache, reusable for any future invariant source.
  - `LifebloodInvariantProvider`: thin orchestrator. Reads `CLAUDE.md` at the loaded project root via `IFileSystem`, delegates parsing to the parser, caches per-root.
- **Source of truth.** `CLAUDE.md` is parsed at runtime; no companion metadata file. Deliberate simplification of the original Phase 8 spike's Option C, eliminating drift between a prose source and a structured mirror. Phase 8C can still add a metadata companion later without breaking the contract.
- **Works on any project.** Lifeblood itself declares 58 invariants. DAWG (production Unity workspace) declares 61. Projects without `CLAUDE.md` get a graceful empty audit.
- **Regression pins**: 43 tests across four files.
  - `ClaudeMdInvariantParserTests`: 17 tests covering both bullet shapes, block termination, duplicate detection, category inference, backtick-code-span-safe title extraction.
  - `InvariantProviderAndHandlerTests`: 14 tests including provider direct tests, handler dispatch, and one end-to-end test via real `lifeblood_analyze` against a minimal Roslyn workspace.
  - `LifebloodClaudeMdSelfTests`: 5 tests that parse Lifeblood's own `CLAUDE.md` and audit the invariant inventory (>= 50 invariants floor, every core category present, known stable ids resolve, no duplicates, no parse warnings).
  - `InvariantParseCacheTests`: 7 tests pinning the generic cache against a fake `IFileSystem`: empty path, missing file, cache hit on unchanged timestamp, invalidation on timestamp change, parser-throws retry, concurrent reads.
- Phase 8 spike published at `docs/plans/invariant-check-spike.md`.

### Added. MCP stdio-loop and ToolHandler search dispatch coverage (`ea08264`)

- **`McpStdioLoopTests`** (3 tests). Every previous MCP test exercised `McpDispatcher` in-process; none spawned the real compiled dll. Closes that gap with tests that boot `Lifeblood.Server.Mcp.dll` via `dotnet <dll>`, speak real JSON-RPC 2.0 over real stdin / stdout, and pin:
  1. `initialize` handshake round-trips through the real reader / writer / dispatcher chain.
  2. `tools/list` returns `result.tools[]`, never `error`. Pins the serialization regression class that `334b47d` fixed.
  3. **Stdout purity.** Every line read from stdout must parse as valid JSON-RPC. Any future `Console.WriteLine` that lands on stdout instead of `Console.Error.WriteLine` (banner, log, stray print) corrupts MCP framing and breaks every client. No in-process dispatcher test can catch this.
- **`Handle_Search_*` tests in `ToolHandlerTests`** (5 new tests). `lifeblood_search` had zero ToolHandler-layer coverage; the dispatch path (args parsing, kinds-array coercion, query string coercion, error envelopes) was untested.

### Known limitation. `lifeblood_dead_code` is experimental / advisory (`INV-DEADCODE-001`)

- **Dogfood against DAWG** surfaced three false-positive classes.
  1. **Method-group references.** A private method passed as a delegate (`new Lazy<T>(Load)`, event handler registration, LINQ `Where(predicate)`) never produces an `InvocationExpressionSyntax` at the call site, so no `Calls` edge is emitted into the referenced method. Example: `BclReferenceLoader.Load` is flagged as dead because its only caller is `new Lazy<>(Load)` in the same type's static field initializer.
  2. **Multi-module canonical-id drift.** Direct invocations of some methods with complex signatures (nullable generics, cross-module source-type parameters) fail to produce `Calls` edges in the full multi-module workspace even though isolated synthetic reproductions work. Example: `ModuleCompilationBuilder.CreateCompilation` is called directly on line 96 of the same file but has zero incoming edges in the graph. **Root cause found and fixed in v0.6.4:** missing implicit global usings in compilation, not canonical-id drift. See v0.6.4 changelog entry.
  3. **Same-class private field reads.** Private fields read from sibling methods on the same type are flagged because the extractor does not emit read-edges at method-to-field granularity.
- **Ship decision.** The tool is marked `[EXPERIMENTAL. ADVISORY ONLY]` in its description. Every response carries `status: "experimental"` plus a `warning` field listing the classes so agents cannot consume findings without seeing the caveat. `Handle_DeadCode_Response_IncludesExperimentalWarning` pins the caveat against future regression.
- **v0.6.4 resolution.** All three classes fixed in v0.6.4. Root cause of class 2 was missing implicit global usings in compilation, not canonical-id drift.
- **Impact on other tools.** `lifeblood_find_references`, `lifeblood_dependants`, `lifeblood_blast_radius`, and `lifeblood_file_impact` inherit the same class-2 gap for the same subset of methods. They are still authoritative for the 95%+ of symbols outside the gap class. Regression tests `ExtractEdges_MethodCall_NullableGenericParameter_SameClass_ProducesCallsEdge` and `ExtractEdges_MethodCall_ComplexSignature_MatchesRealProcessInOrder` in `RoslynExtractorTests` pin the synthetic happy-path.

### Changed

- **Tool count**: 18 to **22** (added `lifeblood_search`, `lifeblood_dead_code`, `lifeblood_partial_view`, `lifeblood_invariant_check`).
- **Port interfaces**: 17 to **22** (added `ISemanticSearchProvider`, `IDeadCodeAnalyzer`, `IPartialViewBuilder`, `IUserInputCanonicalizer`, `IInvariantProvider`).
- **Tests**: 362 to **539**.
- **Duplicate invariant id resolved.** `CLAUDE.md` had two invariants sharing the id `INV-TEST-001` (one under "Testing", one under "Test Discipline"). The drift was caught by `lifeblood_invariant_check` itself, which is why the tool exists. The Test Discipline instance is now `INV-TESTDISC-001`.
- **`Lifeblood.Connectors.Mcp.csproj`** gains `InternalsVisibleTo Lifeblood.Tests`, matching the convention `Lifeblood.Adapters.CSharp.csproj` already uses.
- **docs/STATUS.md ratchets updated**: `portCount: 17 to 22`, `toolCount: 18 to 22`, `testCount: 362 to 539`.
- **Release-wide documentation sweep.** `README.md`, `docs/TOOLS.md`, `docs/ARCHITECTURE.md`, `docs/STATUS.md`, `docs/architecture.html`, and this `CHANGELOG.md` are rewritten to reflect 22 tools, 22 ports, the new invariant-check tool, the compile_check auto-wrap, the resolver wrong-namespace fallback, the search tokenization, and the dead_code experimental caveat.

### Invariants registered in CLAUDE.md

- `INV-RESOLVER-001..004`. Identifier resolution as an Application port. Every read-side tool routes through one resolver. Partial-type unification is a read model.
- `INV-RESOLVER-005`. Wrong-namespace inputs resolve via the trailing short-name segment.
- `INV-VIEW-001..003`. Each language adapter publishes a typed semantic view; consumers take it by reference.
- `INV-TOOLREG-001`. Tool availability dispatch is by typed enum, never by name prefix. Internal registry records and wire-format DTOs are separate types.
- `INV-MCP-003`. Every MCP wire-format constant has exactly one canonical source per side.
- `INV-INVARIANT-001`. `CLAUDE.md` is the single source of truth for architectural invariants; the tool parses it at runtime.
- `INV-DEADCODE-001`. `lifeblood_dead_code` is advisory and ships with three documented false-positive classes. Every response carries the experimental marker and warning.
- `INV-TESTDISC-001`. Tests never silently early-return on precondition failure.

## [0.6.1] - 2026-04-10

Credibility pass after the v0.6.0 release. Closes an MCP-spec interoperability gap, extracts the MCP dispatcher into its own testable class, closes a latent display-string-for-matching bug class in `FindImplementations`, converts silent test skips to explicit `Skip.IfNot` calls that can no longer hide real failures, tightens the architecture ratchet, and syncs the doc numbers that drifted during v0.6.0 preparation. 344 → 362 tests. No behavior change in analyze or any read-side tool.

### Fixed

- **`FindImplementations` used `ToDisplayString()` for cross-assembly type equality** (latent bug class caught during the v0.6.1 deep-audit pass). The same bug class that v0.6.0 Layer 2 of the BCL fix closed in `FindReferences` was still present in `FindImplementations` because the v0.6.0 fix did not sweep every display-string comparison. Display strings can diverge subtly at the source/metadata boundary (nullability, reduced names, attribute round-trips), and using them for *matching*. As opposed to *display*. Is fragile. The deep-audit sweep exposed the bug: after converting silent test skips to explicit `Skip.IfNot` (see below), `FindImplementations_IGreeter_FindsGreeterAndFormalGreeter` ran against the real golden repo for the first time in a while and failed loudly. Fix: compare via canonical Lifeblood symbol IDs (`BuildSymbolId(roslynSymbol) == BuildSymbolId(candidate)`), the same strategy v0.6.0 Layer 3 adopted in `FindReferences`. `CanonicalSymbolFormat` produces identical ID strings for source and metadata copies by design, so cross-assembly matching is correct-by-construction. All other `ToDisplayString()` call sites in the C# adapter were audited and classified. The remaining ones are all stable human-display or namespace-filter uses, not matching comparisons. Also removed the orphaned `ResolveDisplayString` helper that the v0.6.0 `FindReferences` rewrite had left dead.
- **17 silent test early-returns in `WriteSideIntegrationTests`**: every test in the file began with `if (!TryAnalyze(out var graph, out _)) return;`. The `TryAnalyze` helper silently returned `false` on both a legitimate skip condition (golden repo missing) AND a real failure (golden repo present but analysis returned zero symbols). Silent early-return makes both look like passing tests. This directly hid the `FindImplementations` bug above for multiple commits. Fix: replaced `TryAnalyze(out)` with `EnsureAnalyzed()` that uses `Skip.IfNot(precondition, reason)` for missing preconditions (explicit skip via `Xunit.SkippableFact`) and `Assert.True` for real failures (loud fail). All 17 tests converted to `[SkippableFact]`, the silent-return guards removed, and the entire file now either runs for real or skips with a documented reason. Registers `INV-TEST-001`.
- **`notifications/initialized` was rejected by the dispatcher**: the `Program.Dispatch` switch only matched the bare `initialized` method name. Spec-compliant MCP clients sending the canonical `notifications/initialized` form fell through to the `default:` branch and received a `-32601 Method not found` error. Compounding the first violation: that response was a body for a notification. JSON-RPC 2.0 forbids responding to notifications at all. Strict clients saw both violations at once. Tolerant clients (the current Claude Code CLI) silently ignored the errors and the bug went unnoticed.
- **`ToolRegistry.GetTools` used a string-prefix dispatcher** with 8 hard-coded `StartsWith` checks to decide which tools were write-side. `lifeblood_resolve_short_name` was added in v0.6.0 under the write-side comment divider but matched none of the 8 prefixes, so it was never decorated as unavailable even when classified as write-side by its physical position. Root-cause fixed by replacing the string dispatch with a typed `ToolAvailability` enum on `McpToolInfo` (see below). `lifeblood_resolve_short_name` is now classified `ReadSide`. It consults the graph's short-name index, not the Roslyn compilation host.
- **CHANGELOG link-references were stale**: the `[0.6.0]` heading at the top had no matching `[0.6.0]: .../compare/v0.5.1...v0.6.0` reference at the bottom, so the compare link did not render. `[Unreleased]` still pointed at `v0.5.1...HEAD`. Fixed and pinned by a new ratchet test.
- **CHANGELOG was missing `[0.4.0]` and `[0.4.1]` entries entirely**: the file jumped from `[0.5.0]` straight to `[0.3.0]` even though both tags were published. Reconstructed both entries from git log.

### Added. MCP-spec compliance hardening

- **New class `McpDispatcher`** (`src/Lifeblood.Server.Mcp/McpDispatcher.cs`). First-class JSON-RPC / MCP method dispatcher. `Program.cs` used to contain a private `static Dispatch` method on the program entry class; it has been extracted into its own public sealed class so the dispatch logic is testable via the normal public API (no `InternalsVisibleTo`, no reflection, no visibility tricks). `Program.Main` is now a thin stdio I/O loop that constructs the dispatcher and delegates every request to it.
- **`McpDispatcher.SupportedProtocolVersion`** const. The MCP protocol version string (`"2024-11-05"`) is owned by the dispatcher class, single source of truth for the `initialize` response and any future version-gated capability negotiation.
- **`KnownNotifications` set**. `notifications/initialized` (canonical, MCP spec), `initialized` (legacy alias during the deprecation window), `notifications/cancelled`, and `$/cancelRequest` are all recognized. Any entry in the set short-circuits `Dispatch` to return `null` before response construction, guaranteeing JSON-RPC 2.0 compliance. Unknown notifications (any method with `request.Id == null` that is not in the set) also return `null` and log to stderr for operator visibility.
- **`initialize` response explicitly populates `ProtocolVersion` and `Capabilities`** at the construction site in addition to the class-level defaults on `McpInitializeResult`. Belt-and-braces pin so a future edit that strips the defaults does not silently break the wire shape.
- **`serverInfo.version` now reads `AssemblyInformationalVersionAttribute`** (MinVer's canonical output carrying the full semver + provenance form like `0.6.1+abc123`), falling back to the three-part `AssemblyName.Version` form. Previously the dispatcher used the three-part form unconditionally, losing the provenance suffix on non-tagged commits.
- **New `McpProtocolTests.cs`** (10 tests) pinning the dispatcher contract end-to-end: `Initialize_ReturnsSpecCompliantResult`, `Initialize_SerializedJson_HasProtocolVersionAndCapabilities`, `NotificationsInitialized_SpecCompliantForm_ProducesNoResponse`, `NotificationsInitialized_LegacyAlias_ProducesNoResponse`, `UnknownNotification_ProducesNoResponse`, `UnknownRequest_ReturnsMethodNotFound`, `ToolRegistry_EveryToolHasExplicitAvailability`, `ToolRegistry_WriteSideTools_MarkedUnavailable_WhenNoCompilationState`, `ToolRegistry_ReadSideTools_NeverMarkedUnavailable`, `ToolRegistry_ResolveShortName_IsClassifiedReadSide`.

### Added. Typed tool availability dispatch

- **`ToolAvailability` enum** (`ReadSide | WriteSide`) and **`required McpToolInfo.Availability` property**. The `required` modifier (C# 11) makes it a compile error to declare a new tool without setting `Availability`. The invariant is enforced at the language level, not by convention. `ToolRegistry.GetTools(hasCompilationState)` now filters on the typed enum and no longer touches tool name strings.
- Registers `INV-TOOLREG-001`: dispatch is by the typed property, never by name prefix.

### Added. Architecture ratchet for composition roots and ScriptHost

- **`ScriptHost_HasZeroProjectReferences`**. Enforces `INV-SCRIPTHOST-001`. `Lifeblood.ScriptHost` is an isolated child process; it may reference NuGet packages (Microsoft.CodeAnalysis.CSharp.Scripting is load-bearing) but must not reference any Lifeblood project. Without this test the isolation guarantee was documented but not enforced.
- **`CompositionRoot_CLI_UsesOnlyAllowedModules`** and **`CompositionRoot_ServerMcp_UsesOnlyAllowedModules`**. Enforce `INV-COMPROOT-001`. The allowed module set `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}` is declared once as a `HashSet<string>` on `ArchitectureInvariantTests`. Single source of truth. Adding a new module to either composition root requires editing the allowlist, making the scope expansion a conscious architectural decision.

### Added. Documentation ratchet

- **New `DocsTests.cs`** with three ratchet tests pinning `INV-DOCS-001` and `INV-CHANGELOG-001`:
  - `StatusDoc_PortCount_MatchesApplicationPortsDeclarations`. Parses the `<!-- portCount: N -->` HTML comment in `docs/STATUS.md`, counts `public interface I*` declarations under `src/Lifeblood.Application/Ports`, asserts the two match. Single source of truth is the HTML comment.
  - `StatusDoc_ToolCount_MatchesToolRegistryLiterals`. Parses `<!-- toolCount: N -->`, counts `Name = "lifeblood_*"` literals in `ToolRegistry.cs`, asserts the two match.
  - `Changelog_EveryHeadingHasLinkReference`. Parses every `## [X.Y.Z]` heading and every `[X.Y.Z]: ...` link reference, asserts bijection. Pins the bug class where v0.6.0 shipped with stale link refs.

### Changed

- **Port count corrected: 14 → 17** (not `14 → 15` as the v0.6.0 entry claimed). The v0.6.0 release added three port interfaces (`IUsageProbe`, `IUsageCapture`, `ISymbolResolver`) on top of the 14 that existed at v0.5.1. The previous "15" count was counting the usage probe pair as one port unit; the `StatusDoc_PortCount` ratchet counts declared interfaces, which is the accurate semantic. Docs across `README.md`, `docs/STATUS.md`, `docs/ARCHITECTURE.md`, `docs/architecture.html`, and this CHANGELOG have been synced.
- **`docs/architecture.html` stats pill updated**: `1376 symbols, 3822 edges` → `1379 symbols, 3830 edges` (live counts after the v0.6.0 additions). Test count pill updated to `362 tests`.
- **Program.cs** is now a ~70-line composition root. Reduced by ~100 LOC after the `McpDispatcher` extraction.
- **Tests: 344 → 362** (+18 across architecture ratchet, docs ratchet, and MCP protocol tests).

### Dependencies

- **`Xunit.SkippableFact` 1.*** added to `Lifeblood.Tests.csproj`. Enables real skip semantics on xunit 2.x (`Skip.IfNot(condition, reason)`) so missing preconditions turn into explicit Skip states with a reason instead of silent passes. Needed for `INV-TEST-001`.

### Invariants registered in CLAUDE.md

- `INV-MCP-001`. MCP `initialize` response always carries `protocolVersion` and `capabilities`. `protocolVersion` is owned by `McpDispatcher.SupportedProtocolVersion`, never inlined.
- `INV-MCP-002`. Notifications (messages with no `id`) never receive a response body. The `KnownNotifications` set in `McpDispatcher` is the single source of truth for which method names are notifications. Both `notifications/initialized` and the legacy `initialized` alias are accepted.
- `INV-TOOLREG-001`. Every `McpToolInfo` declares its `Availability` explicitly via the required init-only property. The `GetTools(hasCompilationState)` guard filters on `Availability`, never on tool name prefixes. Adding a new tool without setting `Availability` is a compile error.
- `INV-SCRIPTHOST-001`. `Lifeblood.ScriptHost` has zero `ProjectReference`. Ratchet-tested.
- `INV-COMPROOT-001`. Composition roots (`Lifeblood.CLI`, `Lifeblood.Server.Mcp`) reference only the allowlist `{Domain, Application, Analysis, Adapters.CSharp, Adapters.JsonGraph, Connectors.ContextPack, Connectors.Mcp}`. Ratchet-tested via `ArchitectureInvariantTests.CompositionRootAllowedModules`.
- `INV-DOCS-001`. `docs/STATUS.md` port and tool counts match the repository. Pinned via HTML comments as single source of truth and `DocsTests` as ratchet.
- `INV-CHANGELOG-001`. Every `## [X.Y.Z]` heading in `CHANGELOG.md` has a corresponding link reference. Ratchet-tested.
- `INV-TEST-001`. Tests never silently early-return on precondition failure. Missing preconditions turn into explicit `Skip.IfNot(condition, reason)` calls via `Xunit.SkippableFact`. Real failures (presence-but-broken) turn into loud `Assert.True` / `Assert.Fail`. The `TryAnalyze(out) ⇒ bool` silent-return pattern is forbidden.
- `INV-FINDIMPL-001`. `FindImplementations` compares candidates via canonical Lifeblood symbol IDs (`BuildSymbolId`), never via `ToDisplayString()` or `SymbolEqualityComparer.Default`. The canonical-ID comparison is the same strategy `FindReferences` uses, closing the display-string-for-matching bug class across the whole write-side read surface.

## [0.6.0] - 2026-04-10

Three-seam framing after the BCL ownership fix, native usage reporting on every analyze run, and the v0.6.0 doc pack refresh. 329 → 344 tests. 17 → 18 MCP tools. 14 → 17 port interfaces (+`IUsageProbe`, +`IUsageCapture`, +`ISymbolResolver`). `lifeblood_analyze` responses now carry a structured `usage` block with wall time, CPU time, peak memory, GC pressure, and per-phase timings.

### Added. Native usage reporting on every analyze run (LB-INBOX-005)

Every `lifeblood_analyze` response now carries a structured `usage` block
with wall time, CPU time (total, user, kernel), peak working set, peak
private bytes, GC collection counts per generation, host logical core
count, and per-phase timings. No external measurement wrapper required.
The feature is on by default in both CLI and MCP shapes; constructing
`AnalyzeWorkspaceUseCase` without a probe still works and returns a null
`Usage` field with zero overhead.

- **`Lifeblood.Domain.Results.AnalysisUsage`** POCO with `WallTimeMs`,
  `CpuTimeTotalMs`, `CpuTimeUserMs`, `CpuTimeKernelMs`,
  `PeakWorkingSetBytes`, `PeakPrivateBytesBytes`, `HostLogicalCores`,
  `GcGen0Collections`, `GcGen1Collections`, `GcGen2Collections`,
  `Phases[]`, and a derived `CpuUtilizationPercent`. All fields are inert
  data; no `System.Diagnostics` types leak onto the record.
- **`Lifeblood.Application.Ports.Infrastructure.IUsageProbe`** +
  **`IUsageCapture`** port pair, sitting alongside `IFileSystem` in the
  Application layer so the use case stays free of host diagnostics types.
  `IUsageCapture.Stop` is idempotent (INV-USAGE-PORT-002), and the use
  case disposes the capture on the validation-error path so the sampling
  timer does not outlive a failed run.
- **`Lifeblood.Adapters.CSharp.ProcessUsageProbe`** concrete. Uses
  `Process.GetCurrentProcess()` + `Stopwatch` + a background `Timer`
  polling peak RSS at a configurable interval (default 250 ms). Takes an
  initial RSS sample in the constructor so sub-sample-interval runs still
  report a non-zero peak (INV-USAGE-PROBE-001). Captures CPU time as a
  delta of `UserProcessorTime` / `PrivilegedProcessorTime` across the
  run, so the reported numbers are specific to this analyze call and not
  contaminated by earlier work inside the same process.
- **`AnalyzeWorkspaceUseCase`** gains an optional third constructor
  parameter `IUsageProbe? usageProbe`. When non-null, the use case wraps
  its work in a capture, marks `"analyze"` and `"validate"` phase
  boundaries, and returns the snapshot on
  `AnalyzeWorkspaceResult.Usage`. When null, behavior is identical to
  before and `Usage` is null.
- **CLI** prints a `── usage ──` block to stderr after the graph summary
  with a fixed-column layout and InvariantCulture formatting, so the
  output reads the same on every locale. Phase breakdown is shown with
  one line per phase.
- **MCP** `GraphSession.Load` returns a structured JSON response with
  `summary`, `changedFileCount`, and a `usage` object carrying the full
  snapshot. Agents consuming `lifeblood_analyze` now read
  `usage.wallTimeMs`, `usage.peakWorkingSetMb`, `usage.phases[]`, etc.
  without parsing free text. The prior plain-text `"Loaded: ..."`
  response is replaced by the structured form.
- **15 new unit tests** across `UseCaseTests` (3 tests: null usage when
  no probe, populated usage with phase marks, capture disposed on
  validation error) and `ProcessUsageProbeTests` (12 tests: non-zero
  wall time on sleep, CPU time delta on busy work, utilization
  consistency with wall and CPU, peak WS non-zero guarantee, host core
  count match, phase mark order, idempotent `Stop`, `Dispose` after
  `Stop` no-op, `Dispose` without `Stop` no throw, independent captures,
  short-run non-zero peak, GC collection counts non-negative). Tests:
  329 → 344.
- **Invariants documented** in `CLAUDE.md` under the new
  "Usage Reporting (Analyze Pipeline)" section:
  `INV-USAGE-001`, `INV-USAGE-002`, `INV-USAGE-PORT-001`,
  `INV-USAGE-PORT-002`, `INV-USAGE-PROBE-001`, `INV-USAGE-PROBE-002`.

#### Why this shipped instead of living in the improvement inbox

The v0.6.0 doc pack was corrected twice during preparation because the
published memory and timing figures had drifted out of date. The old docs
quoted roughly 90 s wall and 4 GB peak for a 75-module Unity workspace.
The real measured numbers on a 16-core, 32-thread host are 32.6 s wall
and 571 MB peak working set. That drift is inevitable as long as
published benchmarks come from one-off measurement sessions. Putting the
`usage` block on every analyze response makes the docs self-verifying:
any consumer can cite the live output, and the project stays 1:1 honest
without per-release measurement chores. The CLAUDE.md invariants make
this a durable rule. Any future use case that adds measurable work
should reach for the same `IUsageProbe` port, not a bespoke wrapper.

### Added. Plan v4 (three-seam framing for the post-BCL findings)

After v2's BCL fix, two reviewer reports surfaced five remaining findings.
v4 closes them via three architectural seams (one resolver port, one
csproj-driven compilation-fact convention, one adapter semantic view) instead
of five piecemeal patches.

#### Seam #1. `ISymbolResolver` (closes LB-BUG-002, LB-BUG-004, LB-FR-002, LB-FR-003)

- **`Lifeblood.Application.Ports.Right.ISymbolResolver`** + single `SymbolResolutionResult` DTO. Every read-side MCP tool that takes a `symbolId` (lookup, dependants, dependencies, blast_radius, file_impact, find_references, find_definition, find_implementations, documentation, rename) routes through the resolver before doing graph or workspace lookups.
- **`Lifeblood.Connectors.Mcp.LifebloodSymbolResolver`**. Reference implementation. Resolution order: exact canonical match → truncated method form (lenient single-overload) → bare short name. Returns `Outcome` + `Candidates` + `Diagnostic` for ambiguous and not-found cases.
- **Partial-type unification as a read model.** The graph stores raw symbols (one record per partial declaration; last-write-wins remains the storage policy). The resolver computes a deterministic primary file path + the full `DeclarationFilePaths` array by walking the existing `file:X Contains type:Y` edges. **Zero schema change to `Lifeblood.Domain.Graph.Symbol`.** Primary picker rule: filename matches type name → filename starts with `"<TypeName>."` (shortest first) → lexicographic first.
- **Short-name index** added to `SemanticGraph.GraphIndexes` (case-insensitive bucket) + public `FindByShortName(name)` accessor. Built lazily alongside existing indexes.
- **New MCP tool `lifeblood_resolve_short_name`**. Discover canonical IDs from a bare short name when you don't know the namespace.
- **`FindReferencesOptions.IncludeDeclarations`**. Explicit operation policy on `ICompilationHost.FindReferences`. When true, the result includes one synthetic `(declaration)` entry per source declaration site (one per partial for partial types). Two-overload signature preserves backward compat.
- **9 regression tests in `SymbolResolverTests.cs`** including the LB-BUG-002 truncated-id misdiagnosis pin-down (`method:Voice.SetPatch` → resolver canonicalizes → caller graph lookup succeeds).
- **`lifeblood_lookup` response now includes `filePaths[]`**. The full sorted list of partial declaration files. The single `filePath` field is now the deterministic primary, no longer a non-deterministic last-write-wins pick.

#### Seam #2. Csproj-driven compilation facts as a documented convention (closes LB-BUG-005)

- **`ModuleInfo.AllowUnsafeCode`**. New typed bool field, default false, set during `RoslynModuleDiscovery.ParseProject` from `<AllowUnsafeBlocks>` element (case-insensitive on the value to handle Unity's `True` casing). Consumed by `ModuleCompilationBuilder.CreateCompilation` via `WithAllowUnsafe(...)`.
- **`INV-COMPFACT-001..003` documented in CLAUDE.md**. Csproj is the source of truth for module-level compilation options; each fact lives as a typed `ModuleInfo` field set at discovery and consumed at compilation; csproj-edit invalidation flows for free through the existing v2 `AnalysisSnapshot.CsprojTimestamps` infrastructure (re-discovery rebuilds the entire `ModuleInfo`, not just one field).
- Closes the **CS0227 false positive class on Unity packages with `<AllowUnsafeBlocks>`**: any csproj that uses unsafe blocks no longer poisons its semantic model with CS0227, restoring `find_references` / dependants / call-graph extraction for symbols inside `unsafe { ... }` regions.
- **5 regression tests** (3 discovery casing + 2 compilation contract).

#### Seam #3. `RoslynSemanticView` (closes LB-BUG-003)

- **`Lifeblood.Adapters.CSharp.RoslynSemanticView`**. Read-only typed accessor for the C# adapter's loaded semantic state (`Compilations`, `Graph`, `ModuleDependencies`). Constructed once per `GraphSession.Load` and shared by reference across consumers.
- **`RoslynCodeExecutor` refactor**. Primary constructor takes a `RoslynSemanticView`. The view IS the script-host globals object: passed to `CSharpScript.RunAsync<RoslynSemanticView>(code, options, view, ...)` so `lifeblood_execute` scripts can read `Graph`, `Compilations`, `ModuleDependencies` as bare top-level identifiers. Backward-compatible secondary constructor takes only `compilations` for tests and standalone callers.
- **Default script imports extended** with `Lifeblood.Adapters.CSharp`, `Lifeblood.Domain.Graph`, `Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp`. **Default script references extended** to include the assemblies that define the script-globals types so `Graph` / `Compilations` resolve at compile time.
- **`INV-VIEW-001..003` documented in CLAUDE.md**. Each language adapter publishes a typed read-only view of its loaded state; tools that need read access consume the view (not raw fields); the view is shared by reference across consumers.
- **4 regression tests** (1 view-shape sanity + 3 script-globals end-to-end including backward compat for pure-C# scripts).

### Changed

- **All read-side MCP handlers route through `ISymbolResolver`.** Truncated method ids and bare short names now resolve correctly across `lookup`, `dependants`, `dependencies`, `blast_radius`, `find_references`, `find_definition`, `find_implementations`, `documentation`, `rename`. The reviewer's LB-BUG-002 (`method:Voice.SetPatch` returning `[]`) is closed.
- **`find_references` adds optional `includeDeclarations` parameter.** Default false preserves prior behavior.
- **`lifeblood_lookup` response includes `filePaths[]`** alongside the existing single `filePath` (now deterministic for partial types).
- **MCP tool count: 17 → 18** (added `lifeblood_resolve_short_name`).
- **Tests: 310 → 328** (+18 across the three seams).

### Fixed

- **`find_references` / call-graph extraction silently returned 0 results for every method in any Unity / .NET Framework / Mono workspace.** Type-only references (`type:Foo`) worked, method-level references (`method:Foo.Bar()`, `dependants`, call edges) returned empty. Three-layer root cause:
  1. **Resolver silent fallback**: `RoslynWorkspaceManager.FindInCompilation` enumerated *all* methods on a type when no overload matched, returning `methods[0]`. So asking for a method that didn't exist returned an unrelated method's call sites. The wrong target then matched no nodes and the query came back empty.
  2. **Display-string match across the source/metadata boundary**: `RoslynCompilationHost.FindReferences` compared `ISymbol.ToDisplayString()` against the resolved target. Different parameter formatters across source and metadata symbols (driven by Roslyn's default `CSharpErrorMessageFormat` and version drift) silently dropped legitimate call sites.
  3. **BCL double-load** (the dominant cause): `ModuleCompilationBuilder.CreateCompilation` always prepended the host .NET 8 BCL bundle, even for modules that already shipped their own BCL via csproj `<Reference Include="netstandard|mscorlib|System.Runtime">` (Unity ships .NET Standard 2.1; .NET Framework / Mono ship mscorlib). Result: every System type existed in two assemblies. Roslyn emitted CS0433 (ambiguous type) and CS0518 (predefined type missing) on every System usage, the semantic model became unusable, `GetSymbolInfo` returned null at every call site, and every walker tool silently produced empty results.

  Empirical impact on a real 75-module Unity workspace: a single audio-DSP module went from 29,523 errors before to 3 unused-field warnings after. `find_references` for the canonical regression target `method:Voice.SetPatch(VoicePatch)` went from 0 to 18 references, including the previously-invisible `voices[i].SetPatch(patch)` array-indexer call sites. Total graph edges across the workspace: 78,126 to 86,334, restoring +8,208 edges that the broken compilation was silently dropping.

  Fix:
  - **Layer 1 (resolver)**: kind-filtered, name-filtered, signature-strict member lookup with documented contract. Never silently substitute an unrelated member. Lenient escape valves (single overload, no signature given) are explicit.
  - **Layer 2 (matcher)**: replace display-string comparison with canonical Lifeblood symbol-ID comparison via `BuildSymbolId(resolved) == targetCanonicalId`. The graph and the walker now share one builder.
  - **Layer 3 (canonical format)**: `Internal.CanonicalSymbolFormat.ParamType` is the single pinned `SymbolDisplayFormat` for parameter type display strings. Every method-ID builder in the adapter (`RoslynSymbolExtractor`, `RoslynEdgeExtractor`, `RoslynCompilationHost.BuildSymbolId`, `RoslynWorkspaceManager.FindInCompilation`) routes through it. Lifeblood's symbol ID grammar is now owned by Lifeblood, not implicitly inherited from Roslyn's default.
  - **Layer 4 (BCL ownership, INV-BCL-001..005)**: new `BclOwnershipMode` enum on `ModuleInfo`. `RoslynModuleDiscovery` decides ownership ONCE during csproj parsing by inspecting `<Reference Include>` (parsed as assembly identity, handles both bare names and strong-name shapes) and `<HintPath>` basename. `ModuleCompilationBuilder` reads the field and gates host BCL injection. No detection logic at the compilation layer. Decision is single-source-of-truth.
  - **Layer 5 (incremental csproj invalidation, INV-BCL-005)**: `AnalysisSnapshot.CsprojTimestamps` tracks csproj timestamps. `IncrementalAnalyze` checks csproj timestamps before the .cs file loop and forces re-discovery + recompile when a csproj edits. Closes the silent-stale-BclOwnership hole that would otherwise re-introduce the bug under incremental mode.

### Added

- `BclOwnershipMode` enum and `ModuleInfo.BclOwnership` field on `Lifeblood.Application.Ports.Left.IModuleDiscovery`.
- `Internal.CanonicalSymbolFormat`. Single source of truth for parameter type display strings in Lifeblood method symbol IDs.
- `AnalysisSnapshot.CsprojTimestamps` for incremental csproj-edit detection.
- 9 regression tests in `FindReferencesCrossModuleTests.cs` (cross-module class methods, struct methods, partial structs, array-indexer receivers, library-defined parameter types, overload disambiguation, silent-fallback bug class).
- 4 BCL ownership discovery tests in `HardeningTests.cs` (bare Include, strong-name Include, HintPath-only, plain SDK).
- 3 BCL ownership compilation/integration tests in new `BclOwnershipCompilationTests.cs` (ModuleProvided contract, HostProvided contract, end-to-end two-module find_references).
- 2 incremental csproj-invalidation tests in `IncrementalAnalyzeTests.cs`.
- New invariants documented in `CLAUDE.md`: INV-BCL-001 through INV-BCL-005, plus the canonical symbol ID grammar.
- Architectural plan at `.claude/plans/bcl-ownership-fix.md` (v2 with reviewer corrections folded in).

### Changed

- Tests: 293 → 310 (+17 regression tests across discovery, compilation, incremental, and integration layers).

## [0.5.1] - 2026-04-09

### Fixed

- **CS0518 "System.Object is not defined" on multi-module workspaces** (#1): `lifeblood_execute` failed on any code against workspaces with many modules (for example, a 75-module Unity workspace). Three-layer root cause:
  1. `ScriptOptions.Default` contains 25 "Unresolved" named references. Placeholders that never resolve to actual DLLs in a published app.
  2. Adding `compilation.References` (target project's transitive deps) injected Unity's netstandard BCL stubs, conflicting with the host .NET 8 runtime.
  3. Two competing `System.Object` definitions from different BCL flavors caused the script compiler to fail.
  
  Fix: Explicitly load the host .NET runtime's BCL assemblies from the runtime directory (17 core assemblies). Use `WithReferences` (replace) instead of `AddReferences` to drop the useless "Unresolved" defaults. Only add `CompilationReference` per project module. No transitive deps.

### Added

- 5 regression tests for the CS0518 bug class (downgraded multi-module topology).
- `LoadHostBclReferences()`. Resolves host BCL once per process via `Lazy<T>`.

### Changed

- **MCP server deployment**: `.mcp.json` now points to `dist/` (published output) instead of `dotnet run --project`. Decouples running server from build locks.
- Tests: 288 → 293.

## [0.5.0] - 2026-04-09

Cross-assembly semantic analysis. The gap that required grep fallback for cross-module consumer counting is gone.

### Fixed

- **Cross-assembly edges were silently dropped**: `IsFromSource` rejected metadata symbols from other analyzed modules. Renamed to `IsTracked`. Now accepts symbols whose `ContainingAssembly.Name` matches a known workspace module. Resolves F3 (empty cross-module dependency matrix).
- **FindReferences returned empty for cross-assembly symbols**: `SymbolFinder.FindReferencesAsync` doesn't work across AdhocWorkspace project boundaries. Rewritten to direct compilation scan (same proven pattern as `FindImplementations`).
- **FindDefinition/GetDocumentation returned empty for cross-assembly symbols**: `ResolveSymbol` could return a metadata copy (no source location, no XML docs). New `ResolveFromSource` prefers source-defined symbols across all compilations.
- **Dead `IsFromSource` in RoslynCompilationHost**: removed and replaced with wired `IsFromSource` used by `ResolveFromSource` for source preference.
- **Module discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one path.

### Changed

- **CrossModuleReferences capability**: upgraded from `BestEffort` to `Proven`.
- **MinVer auto-versioning**: version derived from git tags via MinVer. No manual bumping. Just tag and push.
- **MCP server version**: `initialize` response now reports actual assembly version instead of hardcoded `"1.0.0"`.
- Tests: 281 → 288.

### Added

- **Cross-assembly edge extraction**: `RoslynEdgeExtractor.KnownModuleAssemblies` enables edges between symbols in different assemblies (References, Implements, Inherits, Calls, Overrides).
- **HintPath DLL loading**: `ModuleInfo.ExternalDllPaths` + `RoslynModuleDiscovery` parses `<HintPath>` from csproj `<Reference>` elements. Unity engine assemblies loaded as metadata references via `SharedMetadataReferenceCache`, resolving thousands of CS0246 false diagnostics.
- **Module dependency threading**: `RoslynWorkspaceAnalyzer.ModuleDependencies` propagated through `RoslynCompilationHost`/`RoslynWorkspaceRefactoring` → `RoslynWorkspaceManager` for ProjectReference-linked workspace (used by Rename).
- 7 new tests: 4 cross-assembly edge extraction (type ref, method call, BCL filter, backward compat), 2 cross-assembly graph integration, 1 cross-assembly FindReferences.
- `.gitattributes` for consistent line endings.
- `global.json` to pin .NET 8 SDK.
- Silent test skips replaced with explicit `Skip` output.
- Bare catches narrowed to typed exceptions in tests and Unity bridge.

## [0.4.1] - 2026-04-08

### Fixed

- **Unity Editor bridge ReadLine hangs**: `LifebloodBridge` sidecar process could hang indefinitely on stalled ReadLine calls with no timeout or liveness check. Every `ReadLine` now has a bounded wait plus process-liveness monitoring, so a stuck child cannot silently freeze the bridge.

## [0.4.0] - 2026-04-08

Release hygiene pass + per-module progress + large-project modes.

### Added

- **Per-module progress reporting**: `lifeblood_analyze` emits progress marks per module via `IProgressSink`, so agents see which module is currently compiling during long analyses.
- **Read-only streaming mode**: `lifeblood_analyze readOnly=true` uses streaming compilation (lower memory, ~4 GB vs ~7 GB on large projects). Write-side tools are unavailable in this mode; intended for large-project read-only analysis.
- **Server GC tuning**: `<ServerGarbageCollection>true</ServerGarbageCollection>` in the build props for better steady-state behavior on long MCP sessions.
- **MinVer auto-versioning**: version derived from git tags by MinVer at build time. No manual version bumping. Tag and push.

### Fixed

- **Unity csproj filesystem scan hang**: when an old-format csproj declared `<Compile Include>` items, the discoverer still performed a recursive filesystem scan of the project directory, which could take minutes on a 75-module Unity workspace. Now trusts the csproj `<Compile>` items and skips the recursive scan when they are present.
- **Discovery merge**: filesystem scan now merges with csproj `<Compile Include>` items instead of choosing one or the other, so hybrid csproj layouts (some items declared, others implicit) produce the correct file set.
- **Release hygiene**: bare `catch {}` narrowed to typed exceptions across test infrastructure and Unity bridge, silent test skips replaced with explicit `Skip` output.

## [0.3.0] - 2026-04-08

Incremental re-analyze, file-level impact, Unity bridge, built-in rule packs.

### Added

- **Incremental re-analyze**: `lifeblood_analyze` with `incremental: true` only recompiles modules with changed files. Seconds instead of minutes. Falls back to full analysis if no previous snapshot exists or if modules were added/removed.
- **File-level edge derivation**: `GraphBuilder.Build()` derives `file:X → file:Y References` edges from symbol-level edges with `edgeCount` property. Evidence: Inferred.
- **lifeblood_file_impact tool**: "If I change this file, what other files break?". Derived from file-level edges.
- **Unity Editor bridge**: `unity/Editor/LifebloodBridge/`. Sidecar MCP server auto-discovered via `[McpForUnityTool]`. All 17 tools available in Unity Editor.
- **Built-in rule packs**: `hexagonal`, `clean-architecture`, `lifeblood`. Resolve by name (`--rules hexagonal`).
- **MCP setup docs**: copy-paste configs for Claude Code, Cursor, VS Code, Claude Desktop, Unity.
- **Editorconfig**: `.editorconfig` with C# conventions.
- **Graceful shutdown**: Ctrl+C / SIGTERM handling in MCP server.
- **ProcessIsolatedCodeExecutor tests**: integration tests with build guard + timeout kill test.
- **10 regression tests**: security scanner (constructors, expression compile), edge deduplication, streaming downgrade.

### Fixed

- **ProcessIsolatedCodeExecutor path with spaces**: quoted project path in dotnet CLI arguments.
- **CI golden repo restore**: WriteSideIntegrationTests skip gracefully when golden repo unavailable.

### Changed

- Tool count: 12 → 17 (7 read + 10 write).
- Self-analysis: 878 symbols → 1148 symbols, 2139 edges → 3196 edges.
- Tests: 214 → 281.
- Architecture screenshot regenerated.

## [0.2.2] - 2026-04-08

### Added

- **75-module Unity workspace production verification**: 43,800 symbols, 70,600 edges, 75 modules, 2,404 types, 34 cycles, around 4 GB peak.

## [0.2.1] - 2026-04-08

### Fixed

- **Edge deduplication in GraphBuilder**: partial classes caused duplicate Overrides/Inherits/Implements edges. `Build()` now deduplicates all edges by `(sourceId, targetId, kind)`.
- **Unity csproj support**: detect `<Compile Include>` items in old-format csproj. If present, use those instead of filesystem scan. Prevents 75-project recursive scan hang.

## [0.2.0] - 2026-04-08

Bidirectional Roslyn, streaming compilation, 45-pass hardening, Python adapter.

### Added

- **Bidirectional Roslyn**: 10 write-side MCP tools. Execute, diagnose, compile-check, find references, find definition, find implementations, symbol at position, documentation, rename, format.
- **Streaming compilation with downgrading**: compile one module at a time in topological order, then `Emit()` → `MetadataReference.CreateFromImage()`. Memory: 32GB → ~4GB for 75-module project.
- **SharedMetadataReferenceCache**: NuGet MetadataReferences deduplicated across modules.
- **NuGet package resolution**: compilations resolve packages from `obj/project.assets.json`.
- **Domain result types**: DiagnosticInfo, CompileCheckResult, CodeExecutionResult, ReferenceLocation, TextEdit.
- **3 new port interfaces**: ICodeExecutor, ICompilationHost, IWorkspaceRefactoring.
- **3 Roslyn implementations**: RoslynCodeExecutor, RoslynCompilationHost, RoslynWorkspaceRefactoring.
- **Compilation preservation**: `AnalysisConfig.RetainCompilations` controls mode (streaming CLI vs retained MCP).
- **Cross-module Roslyn resolution**: compilations built in dependency order via topological sort.
- **Python adapter**: standalone `ast`-based analyzer. Zero dependencies. Self-analyzing.
- **IFileSystem wired**: expanded interface, `PhysicalFileSystem` implementation, injected everywhere.
- **IRuleProvider wired**: `RulesLoader` converted from static class.
- **IProgressSink wired**: `ConsoleProgressSink` writes to stderr.
- **Use case tests**: 8 tests for AnalyzeWorkspaceUseCase and GenerateContextUseCase.
- **Edge extraction tests**: generics, typeof, attributes, return types, C# 9 target-typed new.
- **Golden repo tests**: mini-app fixtures for TypeScript and Python adapters.
- **Dotnet tool packaging**: `Directory.Build.props`, CLI as `lifeblood`, MCP Server as `lifeblood-mcp`.
- **CHANGELOG.md**: version history.

### Fixed

- **Native DLL metadata error**: `LoadBclReferences` filters non-.NET DLLs via `PEReader.HasMetadata`.
- **Session corruption on failed analyze**: `GraphSession.Load` commits state atomically.
- **Symbol resolution from wrong compilation**: FindReferences and Rename resolve from workspace projects.
- **CompileCheck false negatives**: pre-existing diagnostics filtered out.
- **Timeout bypass**: `Task.Run` + `Task.Wait` for hard timeout enforcement.
- **Notification null leak**: `Program.cs` no longer serializes `null` for notifications.
- **RS1024 warning**: Roslyn symbol comparisons use `SymbolEqualityComparer.Default`.
- **Return type edges**: fixed MethodDeclarationSyntax filter that blocked return type references.

### Changed

- Port count: 10 → 14 (3 write-side + 1 blast radius).
- Tests: 109 → 241.
- Self-analysis: 634 symbols → 1057 edges, 888 edges → 2594.
- CI: 4 parallel jobs (build, TypeScript adapter, Python adapter, dogfood).
- Build: 0 warnings, 0 errors.

## [0.1.1] - 2026-04-08

Deep hardening pass. 10 bugs fixed, architecture granulated, AST security scanner, 180 tests.

### Fixed

- **Property symbol ID collision**: `SymbolIds.Property` generated `field:` prefix. Now uses `property:` prefix.
- **GetDiagnostics silent fallback**: non-existent module returned ALL diagnostics. Now returns empty.
- **File.Exists bypassing IFileSystem port**: NuGet resolver used raw `File.Exists`.
- **Property accessor dangling edges**: edge extractor used `get_X`/`set_X` with no matching symbol.
- **MCP parse error response**: malformed JSON-RPC caused no response. Now returns -32700.
- **Notification null leak**: `Dispatch()` returned `null!` for notifications. Now typed as `JsonRpcResponse?`.
- **NuGet catch-all too broad**: bare `catch {}` narrowed to typed exceptions.
- **BCL loader bare catches**: narrowed to typed exceptions.

### Added

- **ScriptSecurityScanner**: Roslyn AST-based security layer with two-layer defense.
- **RoslynWorkspaceManager**: shared workspace infrastructure (~80 LOC dedup).
- **BclReferenceLoader, NuGetReferenceResolver, ModuleCompilationBuilder**: extracted internal components.
- **WriteToolHandler**: extracted 6 write-side MCP handlers.
- **AnalysisPipeline**: moved to Analysis assembly. Single source of truth.
- **59 new tests**: write-side tools, AST security, symbol ID parsing, pipeline, architecture.

### Changed

- Tests: 121 → 180.
- Source files: 58 → 63.
- Average LOC/file: 84 → 80.
- Zero bare catches remaining in source.

## [0.1.0] - 2026-04-07

First public release. Framework is dogfood-verified and CI-green.

### Added

- **Domain**: immutable semantic graph model (Symbol, Edge, Evidence, ConfidenceLevel, GraphBuilder, GraphValidator, GraphDocument).
- **Application**: 10 port interfaces, AnalyzeWorkspaceUseCase, GenerateContextUseCase.
- **Adapters.CSharp**: Roslyn-based workspace analyzer, module discovery, symbol/edge extractors.
- **Adapters.JsonGraph**: universal JSON import/export with full metadata round-trip.
- **Analysis**: CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier, RuleValidator.
- **Connectors.ContextPack**: AI context pack generator, instruction file generator, reading order.
- **Connectors.Mcp**: MCP graph provider with blast radius delegation.
- **Server.Mcp**: MCP server with 6 tools over stdio (JSON-RPC 2.0).
- **CLI**: `analyze`, `context`, `export` commands with centralized validation and exit codes.
- **TypeScript adapter**: standalone Node.js adapter using `ts.createProgram` + TypeChecker.
- **Rule packs**: hexagonal, clean-architecture, lifeblood (self-validating).
- **JSON schemas**: `graph.schema.json`, `rules.schema.json`.
- **Tests**: 109 xUnit tests.
- **CI**: 3 parallel jobs (build, TypeScript adapter, dogfood with cross-language proof).
- **Adapter contribution guides**: Go, Python, Rust (contract and checklist, no implementation code).
- **Documentation**: architecture docs, 11 frozen ADRs, adapter guide, dogfood findings, CLAUDE.md.

[Unreleased]: https://github.com/user-hash/Lifeblood/compare/v0.6.3...HEAD
[0.6.3]: https://github.com/user-hash/Lifeblood/compare/v0.6.1...v0.6.3
[0.6.1]: https://github.com/user-hash/Lifeblood/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/user-hash/Lifeblood/compare/v0.5.1...v0.6.0
[Unreleased]: https://github.com/user-hash/Lifeblood/compare/v0.6.4...HEAD
[0.6.4]: https://github.com/user-hash/Lifeblood/compare/v0.6.3...v0.6.4
[0.6.3]: https://github.com/user-hash/Lifeblood/compare/v0.6.1...v0.6.3
[0.6.1]: https://github.com/user-hash/Lifeblood/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/user-hash/Lifeblood/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/user-hash/Lifeblood/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/user-hash/Lifeblood/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/user-hash/Lifeblood/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/user-hash/Lifeblood/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/user-hash/Lifeblood/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/user-hash/Lifeblood/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/user-hash/Lifeblood/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/user-hash/Lifeblood/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/user-hash/Lifeblood/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/user-hash/Lifeblood/releases/tag/v0.1.0
