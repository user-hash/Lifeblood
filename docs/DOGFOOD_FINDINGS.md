# Dogfood Findings

First successful self-analysis: 2026-04-07. Lifeblood analyzed its own codebase (9 modules at the time, now 11). These are the real issues discovered by running our own tool on ourselves. All findings were fixed in the same session. The numbers below reflect the codebase state at the time of discovery.

**Current state (2026-04-08, session 2, 15 passes):** 958 symbols, 2339 edges, 11 modules, 140 types, 0 violations (16 rules). Three adapters (C#, TypeScript, Python) all self-analyzing and cross-language validated. Process-isolated code execution sandbox added. Evidence.Kind and Evidence.Confidence enforced as `required` at compile time. GraphBuilder drops dangling edges at construction.

### Session 2 Dogfood Findings (2026-04-08)

**DF-S2-1: BlastRadiusAnalyzer depth boundary off-by-one** — maxDepth=1 was including nodes at depth 2. BFS used `> maxDepth` but enqueue happened before depth check. Fixed to `>= maxDepth`. Found by strengthening a weak test that only checked inclusion, not exclusion.

**DF-S2-2: SymbolKind.Property missing from domain enum** — Properties were mapped to `Field` kind but used `property:` ID prefix. Mismatch between graph model and symbol identity. Added `Property` to SymbolKind enum and JSON schema.

**DF-S2-3: Evidence.Default confidence was Proven** — Domain default for all evidence was `ConfidenceLevel.Proven`. Edges without explicit evidence got `Proven` confidence for free. Fixed: evidence must always set confidence explicitly (no default).

**DF-S2-4: JsonEvidence DTO defaulted to Proven** — External adapters that omitted confidence got `Proven`. Fixed default to `BestEffort`.

**DF-S2-5: ProcessIsolatedCodeExecutor pipe deadlock** — WaitForExit called before ReadToEnd. If child process writes more than pipe buffer, WaitForExit hangs forever. Fixed: read stdout/stderr asynchronously before waiting.

**DF-S2-6: AnalyzeWorkspaceUseCase had no validation** — Graph could pass through use case without validation. GraphSession and CLI validated independently, but the use case itself didn't. Fixed: added GraphValidator.Validate() call in use case.

**DF-S2-7: TypeScript adapter used 'high' confidence (removed from enum)** — Schema change removed `high` from ConfidenceLevel. TS compiler caught the mismatch at build time. Fixed capabilities to `proven`/`bestEffort`.

All fixed in-session. Total: 7 findings, 7 fixes, 0 remaining.

### Session 2 Late Findings (passes 6-15)

**DF-S2-8: TS/Python adapters didn't filter dangling edges** — Same bug class as GraphBuilder. Edges to non-existent symbols inflated metrics. Fixed in both adapter output pipelines.

**DF-S2-9: IsFromSource false positive on "System" prefix** — `ns.StartsWith("System")` also filtered user namespaces like `SystemManager`. Fixed to segment-based check: `ns == "System" || ns.StartsWith("System.")`.

**DF-S2-10: TS/Python crossModuleReferences claimed "bestEffort"** — Both are single-module adapters, cannot do cross-module resolution. Fixed to `"none"`.

**DF-S2-11: TS symbol-extractor used `kind: 'field'` for properties** — After adding `SymbolKind.Property` to C# domain, TS adapter wasn't updated. Fixed to `kind: 'property'` with `id: 'property:...'`.

**DF-S2-12: FileInfo/DirectoryInfo/DllImport bypassed blocklist** — Instance methods and P/Invoke not covered by string blocklist. Added `new FileInfo`, `new DirectoryInfo`, `DllImport`, `Marshal.*` patterns.

**DF-S2-13: Lifeblood rule pack incomplete** — Only 11 rules, missing Analysis→Application, Connectors→Analysis boundaries. Expanded to 16 rules covering full hexagonal boundary.

**DF-S2-14: Stale `high` confidence in 4 adapter READMEs + ADAPTERS.md** — Removed `high` from ConfidenceLevel enum but didn't update documentation. Fixed in Go, Rust, TypeScript READMEs and ADAPTERS.md.

**DF-S2-15: CONTRIBUTING.md said "9 frozen ADRs"** — Same drift as ARCHITECTURE.md, fixed to 11.

All fixed in-session. Total: 15 additional findings, 15 fixes, 0 remaining.

## F1: JSON Exporter Silently Drops Fields

**Severity:** Critical — silent data loss

The JSON exporter uses `DefaultIgnoreCondition = WhenWritingDefault`. For enums, the default value is index 0. This means the **first member of every enum is omitted** from exported JSON when it's the active value:

- `SymbolKind.Module` = index 0 → `kind` field **missing** from all 9 module symbols
- `EdgeKind.Contains` = index 0 → `kind` field **missing** from 485 containment edges (74% of all edges)
- `Visibility.Public` = index 0 → `visibility` field **missing** from 350 public symbols

The round-trip test passed by accident: the importer defaults missing enums to index 0, which happens to be the correct value. The test only exercised `EdgeKind.Implements` and `SymbolKind.Type` — both non-zero — so it never caught the bug.

**Root cause:** `WhenWritingDefault` is designed for optional/nullable fields. Using it with enums that have meaningful zero values is a semantic mismatch.

## F2: Architecture Rules Were Never Enforced

**Severity:** Critical — false sense of safety

Both shipped rule packs (`packs/hexagonal/rules.json`, `packs/clean-architecture/rules.json`) use `must_not_reference` (snake_case). The rules loader uses `JsonNamingPolicy.CamelCase`, which expects `mustNotReference`. Result: every rule loaded with `MustNotReference = null` → zero violations, always.

`lifeblood analyze --project . --rules packs/hexagonal/rules.json` reported 0 violations. Not because the architecture was clean — because no rules were actually checked.

**Root cause:** The rules schema (`rules.schema.json`) specifies camelCase. The rule pack files were written in snake_case. No validation step catches the mismatch. No test exercises rule loading from actual pack files.

## F3: Cross-Module Dependency Matrix Is Empty

**Severity:** Moderate — feature gap

The context pack's `dependencyMatrix` (which should show how many type-level edges cross module boundaries) returned 0 entries. Module-level `DependsOn` edges exist (from `.csproj` parsing), but no type-level cross-module edges survive.

**Root cause:** Each module gets its own `CSharpCompilation` without cross-project metadata references. The `IsFromSource` filter correctly rejects edges to types not in the current compilation — but types from other modules aren't in the current compilation. This is a fundamental limitation of the per-module compilation approach, not a bug. The capability descriptor correctly claims `CrossModuleReferences = BestEffort`.

## F4: 57 "Invariants" — Noise, Not Signal

**Severity:** Moderate — undermines trust in output

The context pack's `invariants` array contained 57 entries. Almost all were "`type:X is pure (zero dependencies)`" for enums, value types, and leaf classes. Technically correct, but useless: telling an AI agent that `EdgeKind` is "pure" adds no architectural insight.

**Root cause:** `ExtractInvariants` iterates all Pure-tier symbols. TierClassifier marks any symbol with zero outgoing non-Contains edges as Pure. For a codebase with 80 types, most leaf types qualify. The invariant generator doesn't distinguish "architecturally significant purity" (modules) from "trivially obvious purity" (enums).

## F5: Reading Order Puts High-Instability File First

**Severity:** Minor — counterintuitive output

`ReadingOrderGenerator` sorts by lowest instability first (stable core files), then highest fan-in. But `JsonGraphImporter.cs` (instability 1.0, fan-in 12) appeared first in the reading order. The file-level instability is computed as the minimum instability among child types — if any child type has low instability, the file ranks as stable even though its primary type is highly unstable.

**Root cause:** Min-aggregation of child instability at file level can be misleading when a file contains a mix of stable and unstable types.

## F6: Hotspots Include Composition Roots

**Severity:** Minor — noisy output

The hotspot detector flagged `mod:Lifeblood.CLI` and `mod:Lifeblood.Tests` as hotspots. These are composition roots and test projects — they're *supposed* to have high fan-out. Flagging them as hotspots is technically correct but architecturally meaningless.

**Root cause:** Hotspot detection uses raw coupling metrics without considering the symbol's architectural role (composition root, test project, etc.).

## What Dogfooding Proved

The good news: the core pipeline works. Module discovery, symbol extraction, edge extraction, graph construction, validation, and all five analyzers produced correct results for a real 9-module codebase. The hexagonal boundaries were accurately detected. Domain was correctly identified as the pure leaf.

The bad news: the output layer (JSON export, rule loading, context pack generation) had multiple bugs that were invisible to unit tests. Every one of these bugs could have been caught earlier by running the tool on a real project — including itself.

**Lesson:** Unit tests prove components work in isolation. Dogfooding proves the product works as a product.

## Resolution

All six findings were fixed in the same session they were discovered:

- **F1:** `WhenWritingDefault` replaced with `WhenWritingNull`. All enum, bool, and int fields now always serialize.
- **F2:** Rule packs rewritten to camelCase. New `packs/lifeblood/rules.json` validates Lifeblood's own architecture.
- **F3:** `BuildDependencyMatrix` now uses module-level DependsOn edges with cross-module member edge counting.
- **F4:** Invariants filtered to module-level only (2 invariants instead of 57).
- **F5:** Reading order sort uses stable-first ordering (lowest instability, then highest fan-in).
- **F6:** Hotspot detection excludes modules (composition roots have high coupling by design).

Post-fix dogfood output: 495 symbols, 661 edges, 9 modules, 0 violations, 0 dangling edges, 0 duplicates.

## Second Dogfood: TypeScript Adapter

The TypeScript adapter was the second major dogfood milestone. It analyzed its own source code (a TypeScript project) and produced valid `graph.json` that the C# CLI consumed without modification.

```
# TS adapter analyzes itself
node adapters/typescript/dist/index.js adapters/typescript > graph.json

# Lifeblood C# CLI reads the JSON graph
dotnet run --project src/Lifeblood.CLI -- analyze --graph graph.json
Symbols: 49
Edges:   51
Modules: 1
Types:   9
```

Zero dangling edges. Zero duplicate symbols. Full adapter metadata round-trips (version, language, capabilities).

This proved three things:
1. The JSON protocol works for non-C# languages without modification
2. External process adapters integrate cleanly through `graph.json`
3. The universal model handles TypeScript semantics (classes, interfaces, type aliases, enums, heritage clauses)
