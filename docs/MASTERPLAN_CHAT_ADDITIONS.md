# Chat Agent Architecture Review (2026-04-07)

Additions and corrections to the masterplan from the chat agent's deep review.

## Key Correction: Split Core into Domain + Application

Current `Lifeblood.Core` mixes domain concepts and execution logic. Split it:

**Lifeblood.Domain** (pure, zero deps, the graph model):
- Graph: Symbol, Edge, SemanticGraph, Evidence, enums
- Rules: ArchitectureRule, Violation
- Results: CouplingMetrics, TierAssignment, BlastRadiusResult, GraphMetrics
- Value objects: AdapterCapability, ModuleInfo, AnalysisConfig

**Lifeblood.Application** (orchestration, depends only on Domain + ports):
- AnalyzeProjectUseCase
- ValidateRulesUseCase
- ExportGraphUseCase
- GenerateAgentContextUseCase
- CompareSnapshotsUseCase
- Analyzer pipeline orchestration

## More Granular Ports

### Workspace/source ports
```
IWorkspaceLocator         Find the workspace root and project files
IFileEnumerator           List source files matching patterns
ISourceTextProvider       Read file contents (abstracts disk)
IModuleDiscovery          Discover modules/assemblies/packages
```

### Graph construction ports
```
ILanguageAdapter          Primary adapter (workspace → graph)
IGraphNormalizer          Deduplicate, validate, clean the graph
IGraphMerger              Merge multiple partial graphs
IGraphSerializer          Write graph to format (JSON, etc.)
IGraphDeserializer        Read graph from format
```

### Analysis ports
```
IAnalyzer<TRequest, TResult>   Generic analyzer interface
IEntryPointProvider            What are the entry points (for dead code analysis)
IRuleProvider                  Where do rules come from
ICapabilityPolicy              How to handle low-confidence results
```

### Output ports
```
IReporter                 Format results
IResultSink               Where results go
IProgressSink             Progress reporting for long analysis
```

### Operational ports (plan now, build later)
```
ICacheStore               Cache parsed results for incremental analysis
IIncrementalStateStore    Track what changed since last run
IPluginCatalog            Discover adapter plugins
IRunTelemetry             Analysis run metrics
```

## Application Pipeline

Deterministic execution order:
```
1. Resolve workspace
2. Discover modules
3. Enumerate sources
4. Parse files
5. Normalize symbols/edges
6. Assemble semantic graph
7. Validate graph (reject malformed)
8. Validate rules
9. Run analyzers
10. Aggregate confidence/capabilities
11. Report
```

## Graph Contract Hardening

Lock down before more adapters arrive:
- Canonical symbol ID strategy
- Edge deduplication rules
- SymbolKind/EdgeKind evolution policy
- Required Properties keys for all adapters
- Graph schema versioning
- Confidence propagation rules
- Containment invariants
- GraphValidationService that rejects malformed graphs

## Roslyn Adapter Sub-pieces

Not one big class. Split into:
```
CSharpWorkspaceLocator
CSharpModuleDiscovery
CSharpFileParser
CSharpSymbolProjector
CSharpEdgeProjector
CSharpCapabilityDescriptor
CSharpGraphBuilder
```

## JSON Graph Adapter as Equal Citizen

Not an afterthought. Real adapter with:
- Schema validation
- Graph version upgrades
- Capability metadata ingestion
- Import/export parity tests with Roslyn path

## Three Reporters First
- JsonReporter (machine use)
- HtmlReporter (human inspection)
- SarifReporter (CI/code-scanning ecosystems)

## Golden Repo Fixtures Needed
- Tiny hexagonal app
- Cycle repo
- Interface-heavy boundary repo
- Reflection/metaprogramming repo
- Multi-module purity repo
- Partial class / inheritance repo
- Generics-heavy repo

## Future Connectors (plan as ports now, build later)

**Input:** local filesystem, git repo, GitHub checkout, ZIP archive, prebuilt JSON
**Rules:** local file, repo pack, org registry, CLI override
**Output:** local files, stdout, CI annotations, SARIF, static HTML
**Operational:** disk cache, remote cache, plugin discovery, telemetry

## Chat Agent Rating
- Architecture direction: 8.5/10
- Current executable product: 5.5/10
- Legacy potential if narrowed and executed: very high
