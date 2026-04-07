# Audit: Concrete Holes (2026-04-07)

Chat agent deep code review. All verified. Fix these before claiming anything works.

## Hole 1: README/Code Mismatch
README says analyzers are "needed". But CouplingAnalyzer, BlastRadiusAnalyzer, CircularDependencyDetector, TierClassifier already exist. Meanwhile CLI prints "Not yet implemented" and Adapters.CSharp + Reporters are empty .csproj files.
**Fix:** Make README brutally honest about what is scaffold vs implemented vs working.

## Hole 2: JSON Schema Missing Evidence
Edge.cs has an `Evidence` property. But `graph.schema.json` only has sourceId, targetId, kind, confidence, properties. No evidence field in the wire format. External adapters cannot serialize the proof model.
**Fix:** Add evidence to the JSON schema. Reconcile.

## Hole 3: Schema/Code Name Mismatch
JSON schema uses `discoverSymbols`, `macroExpansion`, `incremental` (camelCase).
C# model uses `CanDiscoverSymbols`, `CanExpandMacros`, `SupportsIncremental`.
Rules README shows `must_not_reference` (snake_case). C# uses `MustNotReference` (PascalCase).
No `rules.schema.json` exists to lock the format.
**Fix:** Add rules.schema.json. Document canonical naming (JSON = camelCase, C# = PascalCase). Add serialization mapping notes.

## Hole 4: Read-Only Graph Violated
SemanticGraph says "analyzers do not modify the graph" (INV-ANALYSIS-002).
But RuleValidator.cs mutates `graph.Edges[e].IsViolation = true`.
**Fix:** RuleValidator should return violations as a separate array. Edge.IsViolation should be on the result, not the graph.

## Hole 5: GraphBuilder Does Not Build Containment
GraphBuilder says it "builds containment hierarchy" but only deduplicates provided symbols and adds module symbols. Does not synthesize Contains edges. Does not use ModuleInfo.Dependencies for module-level edges.
**Fix:** GraphBuilder should wire Contains edges from modules → files → types → members. Should create DependsOn edges between modules from ModuleInfo.Dependencies.

## Hole 6: CouplingAnalyzer Counts Edges Not Distinct Dependants
Claims to follow Robert Martin exactly. But counts all non-Contains edges, not distinct dependers. Repeated references inflate Ca/Ce.
**Fix:** Count distinct source/target symbols, not edge count.

## Hole 7: No Test Project in Solution
Lifeblood.sln has no test project. tests/GoldenRepos has only README.md. No executable contract suite exists.
**Fix:** Add Lifeblood.Core.Tests to solution. Add at least one fixture.

## Hole 8: typeReference EdgeKind is Roslyn-Shaped
EdgeKind.TypeReference feels C#-specific. Not obviously universal.
**Fix:** Consider whether this should be merged into References or kept with a language-agnostic justification.

## Priority Order
1. Make README honest about current state
2. Lock contracts (rules schema, evidence in graph schema, name reconciliation)
3. Fix INV-ANALYSIS-002 violation (RuleValidator mutating graph)
4. Fix GraphBuilder to actually build containment
5. Fix CouplingAnalyzer to count distinct dependants
6. Add test project to solution
7. Wire one real vertical slice (Roslyn → graph → report → golden test)
