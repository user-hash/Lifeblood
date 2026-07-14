# Shared Lifeblood Base And Incremental Agent Updates Masterplan

Date: 2026-07-14

Status: Wave 0 process foundation implemented; repeated DAWG memory receipt is
waiting for a quiescent workspace; path provenance and Wave 1 tool-behavior
source of truth implemented; Wave 2 explicit workspace context, failure-
isolated candidates, and one-reference committed snapshot publication are
implemented; reference-counted read leases are next

Scope: `D:/Projekti/Lifeblood`, dogfooded against Lifeblood and
`D:/Projekti/DAWG`

## Goal

Ship the next Lifeblood update as a production-quality shared analysis service:

- every agent runs the same Lifeblood binary/build contract;
- agents attached to the same workspace share one latest committed semantic
  base instead of retaining duplicate Roslyn graphs and compilations;
- any attached agent may request an incremental refresh;
- refresh work is fingerprinted, coalesced, validated, and published once;
- every later request observes the committed generation or explicitly pins an
  older graph snapshot;
- Lifeblood and DAWG prove correctness, isolation, lifecycle, and memory before
  shared mode becomes the recommended configuration.

This is a major incremental release. The existing stdio mode remains the
rollback path until every promotion gate in this plan passes.

## Decision

The target is **one shared Lifeblood binary and one latest committed semantic
base per canonical workspace analysis identity**.

It is not one mutable graph for every repository. Lifeblood and DAWG contain
different source, profiles, descriptors, and retained compilations; sharing one
`GraphSession` between them would replace one workspace with the other. They
must use separate workspace identities while sharing the same server build and
the same lifecycle/analysis contracts.

Each repository's normal agent configuration must standardize one default
analysis spec, so routine clients converge on the same `BaseKey`. A deliberately
different profile/scope is a named variant with an explicit memory cost, never
an accidental second copy.

For a given workspace identity:

1. The daemon owns the base. Agents do not own private copies.
2. A refresh builds or updates a candidate from an exact analysis key.
3. The candidate is validated before publication.
4. Publication atomically advances generation `g` to `g + 1`.
5. Existing read leases finish on their pinned generation; new requests see the
   new generation.
6. The previous semantic base is disposed after its final lease releases.
7. Identical concurrent refreshes join one in-flight operation.
8. A failed or cancelled candidate never partially replaces the committed
   base.

Steady state therefore retains one semantic/Roslyn base per workspace analysis
identity. A refresh may briefly overlap an old leased base and a candidate; that
overlap must be bounded, measured, and released deterministically. Historical
named snapshots are graph-only by default so snapshot history does not silently
multiply Roslyn heaps.

## Terms And Identities

These concepts must each have one typed source of truth. Wire DTOs may project
them, but must not re-declare their rules.

| Concept | Meaning |
|---|---|
| `ServerBuildIdentity` | Semver, commit/build hash, protocol contract version, process id, and process start time. |
| `WorkspaceKey` | Canonical physical root plus filesystem identity rules. Different worktrees are different keys. |
| `AnalysisSpec` | Profiles, rules, excludes, retention mode, descriptor policy, and other graph-shaping options. Ordering is normalized where it is not semantic. |
| `BaseKey` | `WorkspaceKey + AnalysisSpec`. This owns exactly one latest committed semantic base. |
| `SourceFingerprint` | Authoritative receipt for repository state, descriptors, and source content that can affect the analysis. Dirty state is included; git commit alone is insufficient. |
| `AnalysisKey` | `BaseKey + SourceFingerprint`. Only equal keys may coalesce. |
| `SnapshotId` | Workspace/spec identity plus committed generation and source fingerprint. |
| `ClientLeaseId` | One attached proxy/client lifetime. It is transport lifecycle, not graph identity. |
| `AnalysisRequestId` | One in-flight full or incremental candidate build, possibly awaited by several clients. |

The current `analysisGeneration` remains the monotonic human-friendly counter.
It is not sufficient by itself to identify a base across daemon restarts,
workspaces, or analysis specs.

## Evidence Baseline

### Live product and source verification

| Check | Verified result |
|---|---|
| Released live MCP | `v0.7.12+dbfd871`, 38 tools, initially no graph, private stdio process. |
| Lifeblood full self-analysis | 5,249 symbols, 28,805 edges, 11 modules, 536 types, 288 files, 0 violations, 1 cycle; 17.433 s; 384 MB peak working set. |
| Lifeblood incremental no-op | `incremental-noop`, generation stayed at 1, 0 changed sources, 179 ms. |
| Focused current-worktree tests | 59/59 passed across `GraphSessionGateTests`, `TrackingLedgerTests`, `IntakeLedgerTests`, and `ToolHandlerTests`. |
| Semantic impact | `GraphSession.cs` depends on 33 files and is depended on by 15; `GraphSessionGate.cs` has 4 dependant files; `SharedMcpTransport.cs` depends on 6 files and is depended on only by `Program.cs`. |

The focused suite validates the existing session gate and ledgers. It does not
exercise `SharedMcpTransport` or `McpServerHost` through a real proxy/daemon
transport.

### Two-client shared-mode exercises

The current uncommitted shared-mode build was exercised through real stdio
proxies and named-pipe daemons with unique test pipe names.

| Workspace | Result |
|---|---|
| Lifeblood | Client A performed a full analyze and committed generation 1. Client B immediately observed the same project and generation, returned `incremental-noop`, and client A still observed generation 1. |
| DAWG | Client A performed an Editor+Player full analyze: 87,292 symbols, 338,299 edges, 97 modules, 95.861 s, 2,451 MB peak working set. Client B observed generation 1 and both profiles, then performed an incremental refresh that reported 310 changed source files, took 13.254 s, and committed generation 2. Client A then observed generation 2. |

This proves that the prototype can share one retained session and propagate a
new generation at both small and DAWG scale. It does not prove lifecycle,
version compatibility, failure atomicity, coalescing, or pinned consistency.
The DAWG 310-file incremental result is evidence that accepted change sets and
fingerprints must be observable; its cause was not established and must not be
guessed.

All unique test daemons were stopped after the exercises.

### Configuration audit

- Lifeblood's local Codex config points at `dist-shared/...dll --shared`.
- Lifeblood's `.mcp.json` still points at the ordinary `dist/...dll` process.
- DAWG's Codex and MCP configs invoke plain `lifeblood-mcp` without `--shared`.
- The current live MCP available to this investigation is the released private
  `v0.7.12` server, not the uncommitted shared build.

Therefore agents are not presently guaranteed to use one build or one retained
base. Configuration rollout is a gated implementation phase, not a completed
property.

## Current Architecture Findings

### What is already sound

- `SemanticGraph` is immutable after construction and already supports
  concurrent reads through safely published lazy indexes.
- `GraphSessionGate` provides a server-edge reader/writer gate.
- Full and incremental analyses already expose mode, generation, profile, and
  changed-file counts.
- Incremental detection uses mtime as a prefilter and content hash as the source
  authority; caller-supplied `authoritativeChangedFiles` can narrow source
  scanning without hiding descriptor drift.
- The shared daemon composes exactly one current `GraphSession`, so same-key
  clients really do share the retained heap.
- Ordinary private stdio remains available and can be preserved as a safe
  fallback.

### Open correctness and lifecycle issues

| Priority | Issue | Evidence | Required resolution |
|---|---|---|---|
| P0 | No transport contract tests | No test references `SharedMcpTransport` or `McpServerHost`; current green tests cover only the in-process gate and handler. | Real proxy/daemon integration harness, concurrent requests, disconnects, faults, and cleanup. |
| P0 | Daemon identity is trusted by pipe name | The proxy performs no version, build, workspace-root, or protocol handshake. | Typed handshake before MCP forwarding; reject incompatible reuse without killing unknown processes. |
| P0 | Daemon lifetime is unbounded | Daemon exits only on process cancellation; proxy EOF has no client lease or idle eviction. | Persistent client leases, heartbeat/activity, idle drain, deterministic session disposal, and clean exit. |
| P0 | The proxy cannot model a client lifetime | It opens a new pipe connection for each JSON-RPC frame. | Persistent proxy/daemon connection or explicit client identity on every control/request frame. |
| Resolved in Wave 2 | Candidate publication was not a single state transaction | `WorkspaceSnapshot` now owns graph, analysis, capability, context, timestamp, generation, and the all-or-none compilation ports. `GraphSession` wraps it with adapter/rules/excludes and publishes that complete host state through one reference exchange; failure tests assert the old snapshot reference itself survives. | Complete. Reference-counted leases and deferred disposal remain the separate next lifecycle step. |
| P0 | Workspace identity is implicit | Default shared key is the process working directory; the daemon's singleton session can load any requested project path. | Canonical workspace binding in the handshake and analyze precondition; reject cross-workspace reuse. |
| P0 | Source-generated graph paths used ambient process CWD | A live Lifeblood graph analyzed by a server launched in DAWG attributed `McpJsonSerializerContext` generator files and symbols to `../DAWG/System.Text.Json.SourceGeneration/...`; same-hint outputs from different modules shared one file id. | Shipped locally before Wave 1: one full/incremental `SyntaxTreePathIdentity` seam maps generator hints into a module-qualified `generated/` namespace and keeps them out of disk lifecycle logic (`INV-SOURCEGEN-PATH-PROVENANCE-001`). |
| Partially resolved in Wave 2 | Unity asset reachability also inferred project root through ambient CWD | `WorkspaceContext` now flows from the committed project-backed `GraphSession` through dead-code analysis into `UnityReachabilityAdapter`; relative paths without context fail closed and a non-CWD-root UnityEvent regression is pinned. | Move the context into the immutable committed snapshot object with the remaining session fields; route any future workspace-sensitive provider through the same value. |
| P1 | Identical analyses serialize but do not coalesce | `GraphSessionGate` queues exclusive analyses; a second identical request can repeat the entire analysis. | In-flight registry keyed by the complete `AnalysisKey`. |
| P1 | Multi-call reads can mix generations | Individual envelopes report generation, but callers cannot require one or lease it across a batch. | `expectedSnapshot`/generation precondition and a read-only pinned batch surface. |
| Resolved before Wave 2 | `tools/list` read session state outside the session gate | `McpDispatcher.HandleToolsList` read `HasCompilationState` directly while analyze could replace the session. | `McpDispatcher` no longer owns `GraphSession`; `ToolHandler.GetTools` evaluates registry availability under the shared session gate. Snapshot leases replace this gate read in Wave 2 without changing the dispatcher boundary. |
| Resolved in Wave 1 | Tool labels conflated state and effects | `WriteSide` meant retained compilation required, even for observation or returned edits; only analyze and compile-check needed exclusive access. | Every tool now declares one immutable `ToolBehavior`: hierarchical `sessionRequirement` (including retained compilation), independent `effect`, and independent `sessionAccess`. Legacy read/write fields are derived compatibility aliases. |
| P1 | Incremental acceptance is not explainable enough | DAWG reported 310 changed source files without an itemized/fingerprinted receipt in the response. | Bounded changed-set provenance, descriptor/scope/source fingerprint, and summarize/detail controls. |
| P1 | Rule identity is not refresh-aware | Incremental no-op reuses the prior `AnalysisResult` even when a different `rulesPath` is requested, and stale auto-refresh currently analyzes with no rules. | Put normalized rule identity/content hash in `AnalysisSpec` and `AnalysisKey`; a rule change must rerun rule analysis and publish a new generation without recompiling unchanged source. |
| P1 | Shared capability is advertised too broadly | The uncommitted feature flag reports shared transport support without proving the active transport mode or lifecycle contract. | Report actual mode, protocol version, daemon identity, lease state, and capability version. |
| P2 | One active graph cannot represent named investigation lanes | Editor/Player and platform-specific lanes overwrite the singleton session. | Bounded snapshot catalog; graph-only historical pins by default, explicit cost for extra semantic bases. |
| P2 | Cross-workspace process consolidation is undecided | Current daemon is one singleton workspace session. | First ship one daemon per workspace key; add a multi-workspace supervisor only after correctness and process/memory evidence justify it. |
| P3 | Cross-workspace metadata reuse is tempting but unproven | `SharedMetadataReferenceCache` is currently per analysis. | Defer global cache work until profiles, file identity, version invalidation, and memory benefit are measured. |

## Memory Ledger Audit

The live tracker and intake pass their existing ledger tests, and no intake id
appears in both intake and the live tracker. Those tests do not audit the
archive/live ownership boundary.
The shared-host intake entries are related but not duplicates:

- `LB-INTAKE-20260714-031`: generation-pinned read batches;
- `LB-INTAKE-20260714-037`: named retained investigation snapshots;
- `LB-INTAKE-20260714-038`: daemon identity, leases, and idle eviction;
- `LB-INTAKE-20260714-039`: identical analyze coalescing;
- `LB-INTAKE-20260714-040`: required-state/effect/session-access dimensions.

They converge in this plan but keep distinct acceptance criteria.

One bookkeeping defect did surface: `lifeblood-tracking-archive.md` is a stale
copy of an older live tracker rather than a closed-history-only archive. It
contains its own `Current Snapshot`, stale release counts, active-backlog list,
and the same two partially shipped 2026-05-28 .NET entries that remain active in
`lifeblood-tracking.md`. The required-entry template also omits statuses that
the live model actually uses, including `Partially shipped` and `Receipt`.

This plan does not edit `devmemory/lifeblood-tracking.md`; another agent owns
that update. Archive cleanup should be a separate docs-only atom so it cannot be
confused with the shared-host product change.

## Target Hexagonal Architecture

```text
Agent / MCP client
        |
        v
stdio proxy ---- client lease + identity handshake ---- workspace daemon
                                                        |
                                                        v
                                              WorkspaceCoordinator
                                              /        |         \
                                      snapshot lease  analyze    status
                                            |       coordinator    |
                                            v           |          v
                                  committed snapshot <- publish  telemetry
                                                        |
                                                        v
                                              C# analysis adapter
                                              (Roslyn candidate)
```

### Boundary ownership

| Layer | Owns | Must not own |
|---|---|---|
| Domain | Immutable, language-neutral identity/value types that are true product concepts; graph and result facts. | Named pipes, MCP frames, process ids, locks, Roslyn types, daemon scheduling. |
| Application | Workspace analysis/snapshot use cases and ports; candidate result, committed snapshot descriptor, generation preconditions, and snapshot lease abstractions when transport-neutral. | MCP field names, process discovery, mutexes, named-pipe reconnect policy, Roslyn implementation types. |
| Adapters.CSharp | Roslyn candidate construction, content/descriptor fingerprints, incremental fork/update mechanics, compilation service construction, and deterministic disposal. | Client leases, daemon identity, MCP registry, global process lifetime. |
| Connectors.Mcp | Graph query providers and response projection that remain independent of process hosting. | Daemon lifecycle and Roslyn cache policy. |
| Server.Mcp | Composition root, workspace binding, session coordinator adapter, tool classifications, request routing, proxy/daemon transport, handshake, client leases, idle eviction, drain/shutdown, and wire DTOs. | Duplicate graph/analysis algorithms or Roslyn extraction logic. |

### Single-source-of-truth rules

1. Tool registration stores one immutable `ToolBehavior`: hierarchical
   `sessionRequirement` (where `RetainedCompilation` is the retention
   requirement), independent `effect`, and independent `sessionAccess`.
   Availability, `tools/list`, capabilities, gate routing, and error wording
   derive from it.
2. `AnalysisSpec` canonicalization is shared by the request binder, fingerprint
   builder, coalescing key, snapshot id, and status surface.
3. `WorkspaceSnapshot` is the one committed state object. Graph, analysis,
   capabilities, compilation ports, profiles, paths, fingerprint, generation,
   timestamps, and disposal ownership cannot be published independently.
4. Transport identity is separate from snapshot identity. A daemon restart can
   start at generation 0 without impersonating a previous snapshot.
5. The Roslyn adapter owns source delta mechanics. Agents may submit an
   authoritative changed-file hint, but they do not maintain independent graph
   overlays or compilation caches.

## Committed Snapshot And Refresh Contract

### State machine

```text
Empty
  | full analyze succeeds
  v
Ready(g) -- refresh requested --> Building(candidate for g+1)
  ^                                  |              |
  |                                  | failure      | validated publish
  |                                  v              v
  +----------------------------- Ready(g)       Ready(g+1)
```

- `Ready(g)` is immutable after publication.
- One workspace has at most one publishing analysis operation at a time.
- Readers acquire a lease on a committed snapshot, not on mutable session
  fields.
- Candidate construction never changes the visible committed descriptor.
- Publication is a single reference swap plus generation increment.
- The old snapshot is retired and disposed only after its reader count reaches
  zero.
- Cancellation before publication leaves `Ready(g)` intact.
- Post-publication disposal failure is reported and retried; it does not roll
  the committed generation backward.

### Incremental request flow

1. Canonicalize workspace root and analysis spec.
2. Bind the request to the daemon's workspace identity.
3. Capture descriptor/source-control/source fingerprint evidence.
4. Form the complete `AnalysisKey`.
5. Return the already committed snapshot if the exact key is current and the
   caller permits reuse.
6. Join an identical in-flight request if one exists.
7. Otherwise create one candidate operation.
8. Run adapter incremental analysis only when the prior base and spec are
   compatible; otherwise return a structured rejection or caller-approved full
   fallback.
9. Validate graph, rules, profiles, compilation services, and response facts.
10. Publish once and return one request id, committed snapshot id, generation,
    source fingerprint, effective mode, coalesced/waiter facts, and bounded
    accepted-change receipt.

### Agent behavior

- The first agent may warm the shared base with a full analyze.
- Later agents query it without analyzing again.
- Any agent may request incremental refresh with its known changed files.
- Identical requests join. Different profiles, scopes, retention modes, or
  source fingerprints never coalesce.
- All clients see the new latest generation after publication.
- Evidence-building agents pin `SnapshotId` or `expectedAnalysisGeneration` so
  multi-call reports cannot mix generations.
- Unsaved editor-buffer overlays are deferred. If later required, they must be
  bounded, client-owned, based on an explicit snapshot id, and never silently
  mutate the shared base.

## Host And Transport Contract

### Handshake

Before forwarding normal MCP traffic, proxy and daemon exchange:

- transport protocol version;
- Lifeblood semver and build hash;
- canonical workspace key/root;
- process id and start time;
- supported shared-service features;
- configured idle policy;
- client id and requested capabilities.

An incompatible daemon returns a structured refusal with safe rotate/reconnect
instructions. The proxy never kills a process merely because a pipe name is
occupied.

### Client lease and lifecycle

- One persistent proxy connection owns one `ClientLeaseId`.
- Normal requests update last activity; an optional heartbeat covers idle but
  attached clients.
- Disconnect releases the lease.
- When the last lease releases, the daemon starts an injectable idle timer.
- Reattachment cancels pending idle exit.
- Idle expiry drains requests, disposes all snapshots, releases the pipe/mutex,
  and exits.
- Maintenance shutdown refuses while unrelated leases or analysis waiters are
  active unless an explicit coordinated drain is requested.
- Lifecycle tests use fake time; production timeout sleeps never appear in the
  test suite.

### Deployment topology

The first production topology remains one daemon per canonical workspace key.
This is the smallest design that guarantees DAWG/Lifeblood isolation and solves
same-workspace agent duplication. Both daemons use the same installed binary.

A later supervisor may host several workspace coordinators in one process only
if measurements show meaningful savings beyond the per-workspace solution. The
workspace coordinator contract must make that addition compositional rather
than require another session rewrite.

## Ordered Implementation Waves

Each wave is a natural commit series. Do not start the next wave until the
current wave's tests and receipts pass.

### Wave 0 - Contract Freeze And Black-Box Harness

Purpose: turn the prototype into an observable system before changing its
internals.

1. Add shared-service invariants for workspace binding, build identity,
   candidate publication, lease disposal, and coalescing equality.
2. Add a reusable process-level test driver that starts a unique daemon, starts
   two or more proxies, speaks initialize/tools/list/tools/call, captures stderr,
   and guarantees cleanup.
3. Pin today's proven two-client sharing behavior for a tiny synthetic
   workspace.
4. Add failing-scenario coverage for daemon crash, proxy disconnect, malformed
   frame, occupied pipe, and session disposal.
5. Record single-client and two-client process-tree memory baselines on
   Lifeblood and DAWG. Set release budgets from these receipts before product
   code changes.

Exit gate: the test harness can prove process identity, generation propagation,
and cleanup without relying on manual process inspection.

### Wave 1 - Tool Capability And Access Source Of Truth

Purpose: make scheduling and availability derive from accurate metadata.

Implementation status: complete locally. All 38 tools are ratcheted against an
exact behavior matrix; prerequisite rejection and gate routing consume the
registry; capabilities publish the replacement fields while retaining the
20/18 legacy projection; and `tools/list` now reads live state through the
same gate as tool calls.

1. Replace independently registered `ToolAvailability.ReadSide/WriteSide`
   internally with typed `sessionRequirement`, `effect`, and
   `sessionAccess`; retained compilation is a requirement value rather than a
   redundant fourth axis.
2. Classify every registered tool and ratchet the complete registry.
3. Derive gate routing, availability, capabilities, and missing-prerequisite
   errors from the classification.
4. Preserve current wire behavior additively; deprecate read/write terminology
   only after consumers have the replacement fields.
5. Route `tools/list` and all state-derived capability surfaces through the
   current session gate; Wave 2 substitutes a snapshot lease at that same seam.

Exit gate: no independent name list or handler branch can disagree about a
tool's required state, effect, or concurrency access.

### Wave 2 - Immutable Committed Workspace Snapshot

Purpose: eliminate split-brain session fields and make refresh failure-safe.

1. Introduce the language-neutral snapshot descriptor, explicit workspace
   context, and Application coordinator ports; no provider may reconstruct the
   workspace root from `Environment.CurrentDirectory`.
2. Replace independently mutable `WorkspaceSession`/`GraphSession` publication
   with one committed snapshot object.
3. Make full and incremental adapter paths return complete candidates without
   publishing partial adapter/path/profile state.
4. Add reference-counted read leases and deferred disposal.
5. Preserve monotonic generation inside one daemon and add stable `SnapshotId`.
6. Test failure at every pre-publication phase: discovery, extraction,
   validation, rule analysis, compilation-service construction, and commit.

Exit gate: every injected failure leaves the prior graph, profiles, compilation
services, fingerprint, path, and generation mutually consistent and usable.

Implementation status: steps 1-3 are pinned by explicit `WorkspaceContext`,
immutable `WorkspaceSnapshot`, the one-reference server committed state, and
full/incremental failure rollback under `INV-SNAPSHOT-ATOMIC-PUBLISH-001`.
Step 4 reference-counted leases and deferred disposal is next.

### Wave 3 - Fingerprinted Refresh And Analyze Coalescing

Purpose: ensure agents share work, not only the final heap.

1. Implement canonical `AnalysisSpec`, `SourceFingerprint`, and `AnalysisKey`.
2. Add the per-workspace in-flight analysis registry.
3. Join exactly equal requests; keep different roots, profiles, rules, excludes,
   retention modes, descriptors, and dirty fingerprints separate.
4. Return `analysisRequestId`, coalesced flag, waiter count, requested/effective
   mode, accepted change summary, fingerprint, and committed snapshot id.
5. Make cancellation waiter-aware and failure fan-out deterministic.

Exit gate: N simultaneous identical full or incremental requests call the
analyzer once and publish once; non-identical requests never join.

### Wave 4 - Versioned Persistent Transport And Daemon Lifecycle

Purpose: make the shared host safe to leave enabled.

1. Add persistent proxy connections and the identity handshake.
2. Add client leases, activity/heartbeat, fake-clock idle eviction, drain, and
   explicit maintenance shutdown.
3. Bind the daemon to its canonical workspace key and reject analyze calls for
   another root.
4. Add structured status: build/protocol identity, process start, client count,
   active workspace, snapshot id/generation/profiles, in-flight analyses,
   memory, activity, and idle deadline.
5. Ratchet old-binary/new-proxy refusal and safe recovery.

Exit gate: the last client releases the retained heap and daemon after the
configured idle period, and a stale daemon can never silently serve a new
client build.

### Wave 5 - Snapshot-Pinned Reads And Batches

Purpose: make multi-tool evidence joins consistent under parallel agents.

1. Add optional expected snapshot/generation preconditions to snapshot-backed
   tools.
2. Return a structured retryable mismatch instead of silently moving to latest.
3. Add a bounded read-only batch/query-plan surface that rejects mutating or
   exclusive tools and holds one snapshot lease for the batch.
4. Keep execution serial initially; parallelize only individually proven
   thread-safe tools with stable output order.
5. Ensure every response names snapshot id, generation, workspace, spec, and
   fingerprint through one envelope projection.

Exit gate: a forced concurrent refresh cannot produce a mixed-generation batch
or a falsely successful pinned read.

### Wave 6 - Bounded Snapshot Catalog

Purpose: support parallel investigation lanes without recreating heap
duplication.

1. Add named graph snapshot descriptors and inventory.
2. Default historical pins to graph-only state.
3. Keep at most one latest semantic/Roslyn base per workspace analysis identity.
4. Require explicit budget and lifecycle for any additional semantic variant;
   report its memory cost.
5. Add pin/unpin/evict, drift reporting, LRU/age policy, and hard limits.

Exit gate: Editor/Player evidence lanes are identifiable and revisitable, and
retaining history cannot create an unbounded compilation cache.

### Wave 7 - Lifeblood Then DAWG Rollout

Purpose: deploy one build and one shared base per workspace only after product
gates pass.

1. Publish one local development distribution with a verifiable build identity.
2. Point every Lifeblood client config at that distribution with an explicit
   canonical Lifeblood shared key.
3. Run full, no-op incremental, real-edit incremental, two-client coalescing,
   pinned batch, disconnect, idle eviction, and restart recovery.
4. Repeat with DAWG Editor+Player using an explicit DAWG key.
5. Explain the earlier 310-file refresh through the new accepted-change receipt
   or file a separate verified bug; do not waive it as Unity churn.
6. Compare one, two, and four clients against the Wave 0 CPU/wall/memory budgets.
7. Keep ordinary stdio configuration ready for one-line rollback.

Exit gate: all attached agents observe the same committed generation, a second
agent does not create a second retained Roslyn heap, and daemon idle exit returns
the memory.

### Wave 8 - Release Reconciliation

Purpose: make public contracts match the implementation exactly.

1. Reconcile `STATUS.md`, `MCP_SETUP.md`, `UNITY.md`, tool schemas,
   capabilities, changelog, example configs, and generated evidence.
2. Remove prototype claims that are not backed by invariant and integration
   tests.
3. Clean the tracking archive in a separate docs atom and let the designated
   tracking-file owner update the live ledger.
4. Run full suite, packaging/install smoke, strict JSON/schema tests, docs/link
   checks, Lifeblood self-analysis, and DAWG dogfood one final time.

Exit gate: docs, wire metadata, tests, package contents, and the running binary
all describe the same shared-service contract.

## Required Verification Matrix

### Functional and isolation

- Two clients share one base and generation.
- Lifeblood and DAWG use distinct workspace keys and never observe each other's
  graph, profiles, paths, or generation.
- A daemon rejects a request whose root does not match its bound workspace.
- Full, incremental, incremental no-op, rejection, and approved full fallback
  preserve their existing caller-owned policy.
- A failed candidate leaves the old base fully usable.
- `tools/list`, capabilities, graph queries, and compilation-backed tools see a
  coherent snapshot during refresh.

### Concurrency

- Identical concurrent analyses invoke the adapter once.
- Different analysis specs/fingerprints invoke separately.
- One waiter cancellation does not cancel work required by other waiters.
- Failure fans out equally and does not poison the next request.
- Readers release leases under success, error, timeout, cancellation, and
  broken client connection.
- Old snapshot disposal waits for active readers and happens exactly once.

### Transport and lifecycle

- Initialize and all MCP frames remain protocol-correct through the proxy.
- Version, protocol, and workspace mismatch reject before normal forwarding.
- Last-client disconnect starts idle eviction; reattach cancels it; final expiry
  disposes and exits.
- Crash and occupied-pipe recovery never kill an unidentified process.
- Stderr contains bounded diagnostics and stdout remains pure JSON-RPC.

### Memory and performance

- Steady-state same-workspace memory contains one retained semantic compilation
  set regardless of attached agent count.
- Proxies have a small measured fixed overhead and do not retain graphs.
- Refresh overlap returns to the one-base steady state after lease release.
- Coalesced N-client analyze uses one analysis worth of CPU/allocation.
- Incremental no-op and real-edit receipts stay within the Wave 0 budgets.
- Idle exit releases the daemon and retained heap.

Numeric budgets must be stamped from repeated Wave 0 measurements on the same
machine and dataset. They are release gates, not aspirational prose.

### Compatibility

- Private stdio behavior remains green throughout the rollout.
- Existing tool calls work without snapshot parameters.
- New envelope and tool-classification fields are additive and versioned.
- `readOnly` remains a deprecated compatibility alias until the retention-mode
  replacement completes the schema deprecation policy.

## Rollback And Failure Policy

- Shared mode remains opt-in until Wave 7 passes.
- Client configs can revert to ordinary stdio without data migration.
- No daemon state is required on disk for the first release; restart safely
  returns to generation 0 and requires analyze.
- A handshake mismatch refuses reuse and tells the operator how to rotate; it
  does not terminate an unknown process.
- A candidate failure never evicts the last good base.
- If DAWG memory or correctness budgets fail, stop rollout and keep the private
  mode while fixing the failed wave. Do not weaken the budget in the same atom
  as the failure.
- Disk persistence of Roslyn compilations is out of scope. A later graph-only
  cache may be considered only with exact build/spec/fingerprint compatibility;
  it must not pretend to restore compilation-backed capabilities.

## Proposed Invariant Families

Final ids must be checked against the live invariant index when each atom lands.
The intended families are:

- `INV-SHARED-HOST-IDENTITY-*`
- `INV-SHARED-CLIENT-LEASE-*`
- `INV-SHARED-IDLE-EVICTION-*`
- `INV-WORKSPACE-BINDING-*`
- `INV-SNAPSHOT-ATOMIC-PUBLISH-*`
- `INV-SNAPSHOT-GENERATION-PIN-*`
- `INV-ANALYZE-FINGERPRINT-*`
- `INV-ANALYZE-COALESCE-*`
- `INV-TOOL-REQUIRED-STATE-*`
- `INV-TOOL-SESSION-ACCESS-*`
- `INV-SNAPSHOT-CATALOG-BOUND-*`

## First Implementation Atom After Approval

Start with Wave 0 only:

1. add the shared-service invariant page and route it from the invariant index;
2. add a black-box proxy/daemon process harness with unique pipe names and
   guaranteed teardown;
3. ratchet today's two-client same-generation sharing and private/shared-mode
   isolation, then encode cross-workspace refusal as a named Wave 4 acceptance
   case;
4. add lifecycle seams for fake time and process identity without yet changing
   public configuration;
5. record Lifeblood and DAWG baseline receipts.

Do not switch DAWG or Lifeblood agents to shared mode as part of that atom. Test
seams may change internally, but the first shared-behavior mutation follows only
after the harness can catch incorrect wiring.

## Definition Of 10/10

This update is complete only when all of the following are true:

- one versioned build is used by all configured agents;
- same-workspace agents share exactly one latest semantic base in steady state;
- any agent can refresh it and every client observes the committed generation;
- concurrent identical refreshes perform one analysis;
- generation-pinned evidence cannot mix snapshots;
- failed refreshes preserve the last good base;
- Lifeblood and DAWG are isolated and both pass the full matrix;
- client disconnect and idle expiry deterministically release the retained heap;
- no duplicate tool-state/effect/access or analysis-key definitions exist;
- full tests, package smoke, semantic self-analysis, DAWG dogfood, memory
  receipts, configuration, and documentation agree.
