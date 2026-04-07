# Dogfood Findings

First successful self-analysis: 2026-04-07. Lifeblood analyzed its own codebase (9 modules, 494 symbols, 659 edges). These are the real issues discovered by running our own tool on ourselves.

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
