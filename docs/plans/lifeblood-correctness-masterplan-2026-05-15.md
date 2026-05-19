# Lifeblood Correctness Masterplan (2026-05-15)

**Status:** active plan.

**Scope:** Lifeblood itself. DAWG is a dogfood/repro workspace only; fixes must stay generic, Roslyn-grounded, and covered by Lifeblood self-tests.

**Goal:** turn the current dogfood findings into an ordered Lifeblood roadmap that closes correctness gaps, reduces false-positive noise, and hardens release verification without project-specific hotpatches.

---

## Current release gate

The repo is not release-clean until the verification wall is green.

At the time this plan was written, `dotnet test Lifeblood.sln -c Release --no-restore` reported:

```text
984 total, 983 passed, 0 skipped, 1 failed
```

The failing test was `DocsTests.Changelog_EveryHeadingHasLinkReference`: `CHANGELOG.md` had a `0.7.6` heading without a matching link reference. This is a documentation ratchet doing its job, and it belongs in Stage 0.

---

## Stage 0. Truth ledger and release hygiene

**Intent:** make Lifeblood's own repo tell the same truth as the dogfood tracking sessions before changing behavior.

**Work:**

1. Fix the `CHANGELOG.md` link-reference failure and rerun the Release test wall.
2. Import the open dogfood tracks into Lifeblood's own canonical tracking docs:
   - `LB-TRACK-012`: cross-partial private invocation/reference gap.
   - `LB-TRACK-013`: `port_health` misreads composite inherited ports as empty.
   - `LB-TRACK-014`: `enum_coverage` needs dispatch-table consumption nuance.
   - `LB-TRACK-015`: `dead_code` same-class private field/property FP noise needs triage metadata.
   - `LB-TRACK-016`: dogfood rating / operational UX / MCP restart honesty.
3. Update stale public-facing counts where they are meant to describe current state, especially old `946 tests + 1 skipped` language.
4. Keep historical sections historical; do not rewrite past release notes into current-state claims.
5. Record the observed MCP `Transport closed` failure as an operational limitation unless reproduced as a semantic bug.

**Acceptance:**

- `dotnet test Lifeblood.sln -c Release --no-restore` is green.
- Current tracking entries live in Lifeblood docs, not only downstream project memory.
- Docs distinguish current truth from historical release snapshots.

---

## Stage 1. Canonical symbol identity and cross-partial correctness

**Intent:** close `LB-TRACK-012` at the root. Treat it as a symbol identity and parser consistency problem, not a one-off missing edge.

**Observed risk:**

Lifeblood has multiple symbol-id paths with subtly different behavior:

- `RoslynEdgeExtractor.GetMethodId`
- `RoslynCompilationHost.BuildSymbolId`
- `RoslynWorkspaceManager.ParseSymbolId`
- graph-side resolver behavior in `LifebloodSymbolResolver`

The edge extractor has newer canonicalization logic than some write-side paths. The write-side parser also needs explicit coverage for `.ctor` and `.cctor` ids.

**Work:**

1. Centralize or mechanically align symbol-id generation for methods, accessors, generic methods, constructors, and static constructors.
2. Make `RoslynWorkspaceManager.ParseSymbolId` handle `.ctor` and `.cctor` canonical ids without dot-splitting ambiguity.
3. Add a two-file partial-class fixture:
   - file A declares a private method.
   - file B invokes it.
   - graph dependants, dependencies, `find_references`, and `dead_code` all agree.
4. Add accessor and generic-method parity ratchets where the duplicated code paths currently differ.

**Acceptance:**

- No graph/write-side divergence for cross-partial private calls.
- `find_references` can resolve the same canonical ids that graph extraction emits.
- `dead_code` does not flag a method that is semantically called from another partial declaration.

---

## Stage 2. Dead-code precision and same-class triage

**Intent:** close `LB-TRACK-015` after Stage 1 makes reference data trustworthy.

**Work:**

1. Add same-class consumer metadata to dead-code findings, such as `sameClassConsumerCount`.
2. Consider a capped preview field only if it is useful and cheap; the count is the load-bearing part.
3. Keep the default dead-code result honest: this is triage metadata, not a reason to silently hide findings unless a new filter is explicitly added.
4. Update the warning text so known false-positive classes match the current implementation.

**Tests:**

- Private property/field read from another method in the same class reports `sameClassConsumerCount > 0`.
- Truly unused private property/field reports `sameClassConsumerCount == 0`.
- Cross-class incoming references still remove the symbol from dead-code findings.

**Acceptance:**

- Callers can filter the documented same-class private-read false-positive class from one `dead_code` response.
- The advisory warning no longer claims already-closed gaps are open.

---

## Stage 3. Port health and composite interface semantics

**Intent:** close `LB-TRACK-013` and move port-health logic out of inline server handling.

**Observed risk:**

`port_health` currently inspects direct contained members on the queried type. Composite interfaces that inherit several sub-ports can therefore look empty even when they are architecturally intentional.

`authority_report` may share the same blind spot when it reports per-interface member counts.

**Work:**

1. Extract `port_health` into a real analyzer rather than keeping the algorithm inline in `ToolHandler`.
2. Teach the analyzer to distinguish:
   - truly empty marker interfaces,
   - composite interfaces with inherited members,
   - ordinary interfaces with direct members.
3. Add inherited member counts and inherited source interface counts to the response.
4. Update `authority_report` to either include inherited interface members or expose `ownMemberCount` and `inheritedMemberCount` separately.

**Tests:**

- Empty marker interface remains reported as empty.
- Composite interface reports inherited member count and a composite verdict.
- Implemented inherited members contribute to liveness.

**Acceptance:**

- Composite host ports are no longer mislabeled as vestigial.
- The response tells the caller why an interface is empty versus composite.

---

## Stage 4. Enum coverage for static dispatch tables

**Intent:** close `LB-TRACK-014` without corrupting the existing syntactic meaning of enum coverage.

**Principle:**

`produced`, `consumedComparison`, and `consumedSwitch` are syntactic facts. Do not reinterpret them. Static dispatch-table routing should be additive.

**Work:**

1. Add an additive enum-member field such as `consumedDispatchTableCount` or `dispatchTableReferenceCount`.
2. Reuse static-table extraction or graph provenance where possible instead of adding table-shape string matching.
3. Document that zero switch/comparison consumers can be correct for enum members routed through static dispatch tables.

**Tests:**

- `static readonly Row[] Rows = { new Row(FeatureId.FM, Handler) }` increments the dispatch-table count.
- Switch and comparison counts remain separate.
- Truly unreferenced enum members still report as unreferenced.

**Acceptance:**

- Enum coverage can explain table-routed members without pretending they were switched on or compared.

---

## Stage 5. MCP runtime and redeploy proof

**Intent:** make operational truth as verifiable as semantic truth.

**Work:**

1. Reproduce and classify the MCP `Transport closed` observation.
2. Strengthen the smoke/redeploy path so it proves:
   - the running dist DLL matches the built DLL,
   - `tools/list` succeeds after restart,
   - `lifeblood_analyze` succeeds after restart,
   - the failure mode gives an operator-facing recovery path.
3. Keep this separate from semantic bug tracks unless a semantic failure is proven.

**Acceptance:**

- Release verification includes a live MCP roundtrip, not just a build hash check.

---

## Stage 6. Release wall

**Intent:** prevent "mostly good" from becoming a release.

**Required before an official release decision:**

```text
dotnet test Lifeblood.sln -c Release --no-restore
dotnet build Lifeblood.sln -c Release --no-restore
Lifeblood self-analysis
live MCP analyze roundtrip
DAWG dogfood roundtrip as external verification only
```

No tag should be created or moved by default. If a tag already exists on a commit that later fails the release wall, handle that as an explicit release-management decision instead of silently rewriting history.

---

## Ordering rationale

Stage 0 comes first because release truth must be green before deeper work is trusted.

Stage 1 comes before dead-code triage because same-class dead-code metadata depends on reliable reference identity.

Stage 3 and Stage 4 are independent semantic improvements once Stage 0 is clean, but both should add dedicated ratchets before dogfood claims are upgraded.

Stage 5 can run in parallel with Stages 1 through 4 if it stays operational and does not blur into semantic fixes.
