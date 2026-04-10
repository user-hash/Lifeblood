# Improvement Inbox

Open improvement candidates surfaced during real-world dogfood sessions. Nothing here blocks work. These are friction points where the tools could give better answers, fewer round-trips, or better defaults. Each entry should land in [DOGFOOD_FINDINGS.md](DOGFOOD_FINDINGS.md) once shipped.

Format for each entry: short title, then what was observed, then the suggested fix shape, then why it matters.

---

## LB-INBOX-001. `lifeblood_resolve_short_name` has no fuzzy fallback

**Observed.** Several queries for plausible-sounding short names against a real workspace returned zero results, even though near-matches existed in the loaded graph (different prefix, different trailing word, different namespace stem). The user then had to fall back to grep or `lifeblood_execute` scripts to find the right name.

**Suggested fix.** On a zero-result resolution, return a `Suggestions[]` field with the top N near-matches scored by:

- Exact substring containment of the query inside the candidate's short name (highest weight)
- Levenshtein distance against the candidate short name
- Token-prefix match (e.g. query `FooBar` matches `FooBarBaz`, `BarFoo`, `FoobarHelper`)
- Tie-break by `Kind` filter if one was supplied

The short-name index already exists on `SemanticGraph.GraphIndexes`. This is a read-side scoring pass over the same bucket. No schema change.

**Why it matters.** Short-name resolution is the first tool an agent reaches for when it knows roughly what it's looking for. Zero-result without suggestions forces the agent to leave Lifeblood entirely (grep, IDE, manual recall), which is exactly the failure mode Lifeblood exists to prevent.

---

## LB-INBOX-002. `lifeblood_resolve_short_name` is exact-only with no documented mode

**Observed.** A query for a short name like `FooPool` returned a property whose final identifier was exactly `FooPool` (e.g. `OuterType.FooPool`), but did NOT return a sibling type whose full name *contained* the substring "FooPool" (e.g. `WidgetFooPool`). The exact-vs-contains semantics are not visible from the tool description, and the lookup felt arbitrary depending on whether the user was searching for a substring or an identifier.

**Suggested fix.** Add an explicit `mode` parameter to the tool input schema:

- `mode: "exact"` is the default. Last-identifier exact match, case-insensitive. Current behavior.
- `mode: "contains"` does substring match against the candidate's last identifier.
- `mode: "fuzzy"` uses LB-INBOX-001 scoring and returns ranked candidates.

Document the semantics on each mode in the tool description so agents can reason about when to widen the net.

**Why it matters.** Agents currently can't tell whether a zero-result is "this name does not exist" or "this name exists but I asked for it the wrong way." An explicit mode field removes the ambiguity in one round-trip.

---

## LB-INBOX-003. No semantic keyword search across symbols + XML-doc summaries

**Observed.** When investigating "anything related to <concept X>" in a real workspace, there is no tool that returns a *cluster* of relevant symbols across multiple namespaces. The agent has to know specific names up front, or fall back to grep over source files. The analyzer already has every symbol's short name, and (for documented members) their XML-doc `<summary>` text. This is a missed leverage point.

**Suggested fix.** New read-side tool `lifeblood_search(query, kinds?, limit?)`:

- Tokenizes `query` into keywords (single or multi-word).
- Scores every symbol against the keyword set using:
  - Match in the short identifier name (highest weight)
  - Match in the XML-doc `<summary>` text
  - Match in the parent type or namespace name (lower weight)
- Optional `kinds` filter (`["type", "method"]`).
- Returns the top N symbols with their canonical id, kind, file, line, and the snippet that matched (for explainability).

The XML-doc text is already extracted by `lifeblood_documentation`, so the data is in hand. This is a query layer over existing graph state. No new extraction required.

**Why it matters.** This single tool would replace a large fraction of the grep-then-guess loops that drive agents out of Lifeblood and into the file system. It is the natural next read-side tool after `resolve_short_name` and `lookup`. Same scoring infrastructure, broader query surface.

---

## LB-INBOX-004. `resolve_short_name` does not return canonical IDs per overload

**Observed.** When the user resolved a short name belonging to an overloaded method, the tool returned the type, kind, and file/line, but NOT the full canonical symbol IDs of every overload. The user then had to inspect `dependants` output of an unrelated query to reverse-engineer the parameter signature before they could form a usable canonical id like `method:Foo.Bar.Baz(bool)`. The symbol-ID grammar is the contract Lifeblood owns. Agents should not have to guess it.

**Suggested fix.** When a resolution result targets a method (or any kind that supports overloads), include a `Canonical[]` array in the response with one entry per overload, each containing:

- The full canonical symbol id (built via `Internal.CanonicalSymbolFormat`)
- The parameter list as a display string (using the same `CanonicalSymbolFormat.ParamType` so agents can copy-paste)
- The file and line of that specific overload's declaration

This is a read-side enrichment of the existing `SymbolResolutionResult` DTO. No Domain layer change. The resolver already walks all matching members. It just needs to surface every match instead of collapsing to one.

**Why it matters.** Symbol IDs are the lingua franca of every Lifeblood read-side tool. The grammar is documented (INV-RESOLVER-001..004 plus the canonical format pin in `CLAUDE.md`), but the tool that exists to *discover* canonical IDs from short names hides the most crucial piece, the parameter signature, exactly when overloads make it most important. Closing this loop means every short-name lookup gives the agent a directly-callable id for every overload, no guessing.

---

## LB-INBOX-005. `lifeblood_analyze` should report clear usage and timings natively

**Observed.** Running `lifeblood analyze` against a real 75-module Unity workspace prints progress (`[1/3] Analyzing workspace`, etc.), final counts (symbols, edges, modules, types, cycles), and nothing else. There is no wall time, no peak memory, no CPU time, no per-phase breakdown. To get those numbers today you have to wrap the process with an external measurement harness (PowerShell `Start-Process` + poll, `/usr/bin/time -v`, or similar). Agents investigating performance therefore cannot self-serve and have to ask the operator for numbers that the process itself already has at its fingertips.

The same gap applies to `lifeblood_analyze` via MCP. The tool result carries graph counts but not the run's wall time, peak RSS, or phase breakdown.

**Suggested fix.** Add a `usage` block to every `lifeblood_analyze` response (both CLI and MCP tool), populated by the run itself from sources it already has or can cheaply add:

- `wallTimeMs` (Stopwatch around the top-level analyze call)
- `phases[]` (`discover`, `parse`, `compile`, `extract`, `build`, `validate`, each with `durationMs` and optionally `cpuMs`)
- `peakWorkingSetBytes` (poll `Process.GetCurrentProcess().WorkingSet64` from a background timer during the run, or sample at phase boundaries)
- `peakPrivateBytesBytes`
- `cpuTimeTotalMs` (`Process.GetCurrentProcess().TotalProcessorTime` delta)
- `cpuTimeUserMs` / `cpuTimeKernelMs`
- `gcCollectionCounts[gen0|gen1|gen2]`
- `hostCores` (from `Environment.ProcessorCount`)
- `derived.cpuUtilizationPercent` (convenience: `cpuTimeTotalMs / wallTimeMs * 100`)

For the CLI, print a one-screen usage block after the existing graph counts. For the MCP tool, include it in the structured result under a `usage` key so agents can read it without parsing text.

No external dependencies. `System.Diagnostics.Process` and `System.Diagnostics.Stopwatch` are enough. Polling peak working set from a `Timer` callback every 250 ms inside the analyze call is cheap and gives accurate peak figures. Phase-level Stopwatch timing is effectively free.

**Why it matters.** Three things:

1. **Self-serve performance investigations.** Agents today cannot answer "is this slow because of compile time, extraction time, or validation time?" without touching external tooling. The phase breakdown turns that into a one-shot query.
2. **Regression detection.** Every time the analyze pipeline changes, the wall-time and peak-RSS numbers should shift predictably. A usage block in the response makes it trivial to write ratchet tests that fail if, say, peak RSS on a canonical fixture grows by more than 10% between commits.
3. **Honesty in the docs.** The published memory figures in `docs/STATUS.md`, `docs/UNITY.md`, and `README.md` come from manual measurement sessions that drift out of date quickly. If every analyze response carries fresh usage numbers, the docs can cite the output directly, and "1:1 honesty" stays 1:1 without per-release measurement chores. A real measurement taken during v0.6.0 preparation on a 16-core, 32-thread host showed 36.2 s wall, 56.2 s total CPU (155% of one core, around 5% across all 32 threads), and a 556 MB peak working set on a 75-module 400k LOC Unity workspace. Those numbers contradict the "~90 s, ~4 GB" figures that older docs quote. A native usage block would have caught the drift immediately.

---

## How entries land here

If you find a friction point during a real session:

1. Reproduce it once with a minimal query against a workspace you trust.
2. Write the entry as: title, observation, suggested fix, why it matters.
3. Anonymize all consumer-specific names. The Lifeblood repo carries no leakage from downstream workspaces. Describe shapes (`FooType`, `BarMethod`, `OuterType.PropertyName`) instead of real identifiers.
4. Keep entries narrow. One observation, one fix shape per entry. Cross-reference with `LB-INBOX-NNN` ids when entries are related.
5. When the fix ships, move the entry into [DOGFOOD_FINDINGS.md](DOGFOOD_FINDINGS.md) under the next session header.
