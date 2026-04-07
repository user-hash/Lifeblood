# Architecture Decision Records

Frozen choices for framework v1. These do not change without a major version bump.

## ADR-001: Domain has zero dependencies

**Decision:** `Lifeblood.Domain` has no PackageReferences and no ProjectReferences.
**Reason:** The graph model is the universal center. Any dependency makes it language/tool specific.
**Enforced by:** `ArchitectureInvariantTests.Domain_HasZeroDependencies`

## ADR-002: Application depends only on Domain

**Decision:** `Lifeblood.Application` references only `Lifeblood.Domain`.
**Reason:** Application defines ports. If it references adapters/connectors, hexagonal boundaries collapse.
**Enforced by:** `ArchitectureInvariantTests.Application_DependsOnlyOnDomain`

## ADR-003: One confidence model

**Decision:** `ConfidenceLevel` enum (None/BestEffort/High/Proven) is the single trust model. Used by both `Evidence` and `AdapterCapability`.
**Reason:** Float confidence was ambiguous and not comparable across adapters. Enum tiers are well-defined.
**Changed in:** Hardening Phase 3 (2026-04-07)

## ADR-004: Properties are IReadOnlyDictionary

**Decision:** `Symbol.Properties` and `Edge.Properties` expose `IReadOnlyDictionary` on the public surface.
**Reason:** INV-GRAPH-004 (read-only after construction) was convention-only. Now type-enforced.
**Changed in:** Hardening Phase 3 (2026-04-07)

## ADR-005: Adapters do not reference other adapters

**Decision:** No adapter project may reference another adapter project.
**Reason:** Each adapter is an independent left-side vein. Cross-adapter coupling would break pluggability.
**Enforced by:** `ArchitectureInvariantTests.Adapters_DoNotReferenceOtherAdapters`

## ADR-006: Connectors do not reference adapters

**Decision:** No connector project may reference any adapter project.
**Reason:** Right side must not depend on left side. Both depend inward on Application ports.
**Enforced by:** `ArchitectureInvariantTests.Connectors_DoNotReferenceAdapters`

## ADR-007: Capability claims must be honest

**Decision:** Adapter capabilities must reflect actual extraction, not aspirational claims.
**Reason:** Framework consumers rely on capability declarations to decide what analysis is trustworthy.
**Example:** Roslyn adapter lowered `OverrideResolution` to `None` and `CrossModuleReferences` to `BestEffort` during hardening because those extractions are not yet implemented.

## ADR-008: CLI is the composition root

**Decision:** Analysis orchestration (`AnalysisPipeline`) lives in CLI, not Application.
**Reason:** Application (INV-APP-001) cannot reference Analysis. The composition root is the correct place to wire concrete analyzers. If a portable orchestrator is needed later, a new assembly that references both Application and Analysis can be created.

## ADR-009: JSON protocol is the universal adapter contract

**Decision:** External adapters communicate only via `schemas/graph.schema.json`.
**Reason:** Requiring C# for adapters would kill universality. JSON is the lingua franca.
**Enforced by:** JsonGraphImporter/Exporter + round-trip tests.
