# Lifeblood Architecture Masterplan

10 stages from founding scaffold to 10/10 semantic architecture engine.

---

## Current State (v0.1)

What exists: graph model, port stubs, 3 partial analyzers, JSON schema, rules packs, no working adapter, no working CLI. Architecture is directionally right but the primary port (`ICodeParser` at file level) is too narrow for real semantic analysis. Roslyn needs workspace/compilation scope, not single-file parsing.

What the chat agent flagged correctly: "the repo is architecturally ahead of its implementation." True. Fix that.

---

## The Fix: Workspace-Level Primary Port

The current `ICodeParser.Parse(filePath, sourceText)` is a file-at-a-time contract. That works for syntax extraction but breaks for semantic resolution. Roslyn resolves types across the entire compilation. A file in isolation cannot tell you if `Foo` is `MyApp.Domain.Foo` or `MyApp.Infrastructure.Foo`.

**The primary adapter port must be workspace-scoped:**

```csharp
IWorkspaceAnalyzer
  AnalyzeWorkspace(projectRoot, config) → SemanticGraph

// ICodeParser stays as an internal helper inside adapters,
// not as the primary contract.
```

This is the single biggest architectural correction.

---

## Full Port Map (Maximum Granularity)

### Input Ports (how data enters the core)

```
IWorkspaceAnalyzer          Primary adapter port. Workspace → SemanticGraph.
                            Roslyn adapter, TS adapter, Go adapter all implement this.

IGraphImporter              Read a pre-built graph from JSON.
                            This is how external adapters (Python, Rust) feed the core.
                            They run as separate processes, output graph.json, core reads it.

IRuleSource                 Where architecture rules come from.
                            File-based (lifeblood.rules.json), embedded pack, or API.
```

### Output Ports (how results leave the core)

```
IReportSink                 Where analysis results go.
                            JSON file, HTML report, CI output, stdout.

IGraphExporter              Serialize the graph for external consumption.
                            JSON (for visualization), DOT (for Graphviz), custom formats.

IAgentContextGenerator      THE KILLER FEATURE.
                            Generates context packs for AI agents:
                            high-value files, boundaries, invariants, hotspots,
                            reading order, blast radius, dependency map.
                            This is what makes Lifeblood the glue between AI and codebases.
```

### Query Ports (how consumers ask questions about the graph)

```
ISymbolQuery                Find symbols, resolve references, navigate the graph.
                            "Give me all types in this module."
                            "What does this symbol depend on?"

IDependencyQuery            Dependency-specific queries.
                            "What is the blast radius of changing this file?"
                            "Show me all reverse dependencies of this interface."

IRuleQuery                  Rule-specific queries.
                            "Which rules apply to this symbol?"
                            "Is this edge violating any rule?"
```

### Infrastructure Ports (environment abstractions)

```
IFileSystem                 Read source files. Abstracts disk access.
                            Enables testing with in-memory file systems.

ILogger                     Logging. Analysis should not use Console.Write directly.

IClock                      Time. For cache invalidation, staleness detection, timestamps.

ICache                      Cache parsed results. Incremental analysis reads from cache
                            instead of re-parsing unchanged files.
```

---

## Full Analysis Pass Map

All stateless. Input: SemanticGraph + config. Output: typed result. No side effects.

### Structural Analysis
```
CouplingAnalyzer            Fan-in, fan-out, instability per symbol.
CircularDependencyDetector  Tarjan's SCC. Cycles in the dependency graph.
HubBridgeDetector           Betweenness centrality. God classes and bottlenecks.
DeadCodeAnalyzer            Unreachable from entry points.
```

### Architecture Analysis
```
TierClassifier              Pure / Boundary / Runtime / Tooling per module and file.
BoundaryLeakAnalyzer        Finds edges that cross tier boundaries illegally.
RuleValidator               Checks edges against architecture rules.
InvariantVerifier           Checks if INV-xxx rules hold in actual code.
```

### Risk Analysis
```
BlastRadiusAnalyzer         What breaks if you change this symbol.
ChangeRiskAnalyzer          Combines coupling + complexity + churn frequency.
StabilityAnalyzer           Tracks instability trends across snapshots over time.
```

### AI Context
```
AgentContextBuilder         Produces context packs for AI agents.
                            Most impactful output of the entire system.
```

---

## Application Layer (Use Cases)

Orchestrate adapters, core, and output. No business logic here.

```
AnalyzeWorkspaceUseCase     Discover modules → parse → build graph → run all analysis → report.
                            This is the main CLI command.

ValidateRulesUseCase        Load rules → validate against graph → report violations.
                            CI integration: exit code 0 = clean, 1 = violations.

CompareSnapshotsUseCase     Load two graphs (before/after) → diff → report changes.
                            "What changed architecturally since last release?"

GenerateAgentContextUseCase Analyze graph → produce context pack for AI.
                            Output: JSON with high-value files, boundaries, reading order.

ExportGraphUseCase          Serialize graph to JSON/DOT/custom format.
                            For visualization tools, dashboards, external analysis.
```

---

## 10-Stage Masterplan

### Stage 1: Architecture Correction
**Fix the foundation before building on it.**

- [ ] Promote primary port from `ICodeParser` (file) to `IWorkspaceAnalyzer` (workspace)
- [ ] Demote `ICodeParser` to internal adapter helper
- [ ] Add `IGraphImporter` port (read JSON graphs from external adapters)
- [ ] Add `IReportSink` port
- [ ] Add `IFileSystem` port (enable testability)
- [ ] Add `IAgentContextGenerator` port (define the killer feature early)
- [ ] Update CLAUDE.md invariants for new port structure
- [ ] Verify: Core.csproj still has ZERO dependencies

**Exit criteria:** All ports defined. Core compiles. No adapter code in core.

### Stage 2: C# Roslyn Adapter (Reference Implementation)
**Prove the architecture works on real code.**

- [ ] Implement `RoslynWorkspaceAnalyzer : IWorkspaceAnalyzer`
- [ ] Module discovery from .csproj / .sln / .asmdef files
- [ ] File parsing: symbols (types, methods, fields) + edges (imports, inheritance)
- [ ] Roslyn semantic model: type resolution, call resolution, implementation edges
- [ ] Evidence on every edge (syntax vs semantic, source span, confidence)
- [ ] Capability declaration: proven for type/call/override resolution
- [ ] Test on a small real C# project (not DAWG yet, something small first)

**Exit criteria:** Parse a real C# project. Produce a valid SemanticGraph. All edges carry evidence.

### Stage 3: CLI + JSON Reporter (End-to-End Path)
**Make it runnable. Someone types a command, gets a result.**

- [ ] `lifeblood analyze --project <path>` uses Roslyn adapter
- [ ] `lifeblood analyze --graph <json>` uses JSON importer (external adapters)
- [ ] `lifeblood rules --project <path> --rules <rules.json>` validates rules
- [ ] `lifeblood export --project <path> --output graph.json` exports graph
- [ ] JSON reporter: machine-readable output
- [ ] Human-readable console summary
- [ ] Exit codes: 0 = clean, 1 = violations, 2 = error

**Exit criteria:** `lifeblood analyze --project ./my-app --rules hexagonal.json` works end to end.

### Stage 4: Golden Repo Test Suite
**Trust through testing. Every adapter must pass the same contract.**

- [ ] TinyHexagonalApp fixture: 3 modules (Domain pure, App, Infrastructure)
- [ ] CircularDependencyRepo fixture: intentional cycles
- [ ] DeadCodeRepo fixture: unreachable symbols
- [ ] BoundaryViolationRepo fixture: forbidden cross-tier edges
- [ ] `expected.json` per fixture: what analysis MUST find
- [ ] Contract test runner: adapter produces graph, verify against expected
- [ ] C# adapter passes all fixtures

**Exit criteria:** `dotnet test` passes. All fixtures verified. Any new adapter can be tested the same way.

### Stage 5: Core Analyzers (Full Suite)
**The analysis that makes the graph valuable.**

- [ ] CouplingAnalyzer: fan-in, fan-out, instability (already started)
- [ ] CircularDependencyDetector: Tarjan's SCC (already started)
- [ ] BlastRadiusAnalyzer: reverse dependency walk (already started)
- [ ] TierClassifier: Pure/Boundary/Runtime/Tooling (already started)
- [ ] DeadCodeAnalyzer: mark entry points, walk reachability, report unreachable
- [ ] HubBridgeDetector: Brandes betweenness centrality
- [ ] BoundaryLeakAnalyzer: find edges crossing tier boundaries
- [ ] RuleValidator: must_not_reference, may_only_reference (already started)
- [ ] All analyzers tested against golden repos

**Exit criteria:** All 8 analyzers work. Golden repo tests verify each one.

### Stage 6: Agent Context Generation (The Killer Feature)
**This is what makes Lifeblood the glue between AI and codebases.**

- [ ] `IAgentContextGenerator` implementation
- [ ] Output: JSON context pack containing:
  - High-value files (highest coupling, most dependants)
  - Architecture boundaries (which modules are pure, which are coupled)
  - Invariants extracted from rules
  - Hotspots (high complexity + high coupling + high change frequency)
  - Recommended reading order (topological sort by importance)
  - Blast radius map for critical symbols
  - Dependency matrix summary
- [ ] `lifeblood context --project <path>` CLI command
- [ ] Format designed to be pasted into CLAUDE.md or AI instruction files
- [ ] Test: generate context for golden repo, verify it is useful

**Exit criteria:** `lifeblood context --project ./my-app` produces a context pack an AI agent can consume.

### Stage 7: Snapshot Diff
**Track architecture over time. Catch drift.**

- [ ] `lifeblood snapshot --project <path> --output snapshot-v1.json`
- [ ] `lifeblood diff --before snapshot-v1.json --after snapshot-v2.json`
- [ ] Diff output: new symbols, removed symbols, new edges, removed edges, new violations, resolved violations
- [ ] Integration: run in CI, fail if new violations appear
- [ ] Ratchet support: violation count can only decrease

**Exit criteria:** Diff two snapshots. Report what changed. CI can enforce ratchets.

### Stage 8: Rules Engine Upgrade + Packs
**From simple glob matching to a real declarative rule system.**

- [ ] YAML rule format (in addition to JSON):
  ```yaml
  tiers:
    Domain:
      may_depend_on: []
    App:
      may_depend_on: [Domain]
    Infrastructure:
      may_depend_on: [App, Domain]
  ```
- [ ] Tier-aware rules (not just namespace patterns)
- [ ] Naming constraints (e.g., "ports must start with I")
- [ ] Forbidden symbol reference rules (e.g., "no Debug.Log in Domain")
- [ ] Rule packs: hexagonal, clean-architecture, DDD, monorepo
- [ ] `lifeblood rules --pack hexagonal` applies a built-in pack
- [ ] Custom rule packs: users define their own

**Exit criteria:** Rule packs work. Users can apply hexagonal rules with one flag.

### Stage 9: TypeScript Adapter (Prove Universality)
**Second language proves the architecture is truly language-agnostic.**

- [ ] TypeScript adapter using `ts.createProgram` and `TypeChecker`
- [ ] Module discovery from `tsconfig.json` / `package.json`
- [ ] Symbol extraction: types, functions, imports, exports
- [ ] Semantic edges: type resolution, call resolution, implementation
- [ ] Capability declaration: high for type/call resolution
- [ ] Passes all golden repo fixtures (TypeScript versions)
- [ ] CLI works: `lifeblood analyze --project ./my-ts-app`

**Exit criteria:** Analyze a real TypeScript project. Same analyzers, same rules, same output format.

### Stage 10: Extension Points + Ecosystem
**Make it extensible. Let the community build on it.**

- [ ] Plugin system: drop an adapter DLL, it is discovered automatically
- [ ] CI templates: GitHub Actions, GitLab CI, Azure DevOps
- [ ] IDE integration: VS Code extension that shows tier/coupling in editor
- [ ] Visualization: web dashboard reading graph.json
- [ ] NuGet package: Lifeblood.Core as a library others can embed
- [ ] Contributor guide: how to build an adapter, how to add an analyzer
- [ ] Adapter certification: automated test that verifies adapter quality

**Exit criteria:** External contributors can build and publish adapters without touching core.

---

## The Killer Positioning

Lifeblood is not a linter. Not a code reviewer. Not a documentation tool.

**Lifeblood is the semantic layer between AI agents and codebases.**

Without it, AI agents work at text level. They grep, they guess, they break things they cannot see.

With it, AI agents see the codebase the way an IDE does: types, references, boundaries, dependencies, blast radius, authority. They know what matters before they write code.

**Stage 6 (Agent Context Generation) is the feature that makes this real.** Everything before it is infrastructure. Everything after it is scale.

---

## Architecture Invariants (updated for masterplan)

```
INV-CORE-001:  Core has zero external dependencies. Ever.
INV-CORE-002:  Core has zero references to any adapter.
INV-CORE-003:  All analysis operates on SemanticGraph only.
INV-CORE-004:  All analyzers are stateless. No side effects.
INV-PORT-001:  Primary adapter port is workspace-scoped, not file-scoped.
INV-PORT-002:  File parsing is internal to adapters, not a core contract.
INV-PORT-003:  External adapters communicate via JSON graph schema only.
INV-GRAPH-001: SymbolKind enum is language-agnostic.
INV-GRAPH-002: Language-specific metadata goes in Properties, not new fields.
INV-GRAPH-003: Every edge carries Evidence (kind, adapter, confidence, source span).
INV-ADAPT-001: Every adapter declares capabilities honestly.
INV-ADAPT-002: C# adapter is the reference. Most complete, best tested.
INV-ADAPT-003: Adapters can be in-process (C#) or out-of-process (any language → JSON).
INV-TEST-001:  Every adapter passes the same golden repo contract tests.
INV-TEST-002:  Every analyzer is tested against golden repos.
```
