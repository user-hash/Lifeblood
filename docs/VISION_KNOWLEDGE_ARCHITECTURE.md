# Vision: Knowledge Architecture Beyond Code

> Saved 2026-04-07. Do not act on this now. Lifeblood for code must be proven first.
> This is the founding charter for the knowledge track when the time comes.

## The Insight

The core abstraction (symbols + edges + tiers + rules + evidence + guided retrieval) is not code-specific. Any structured knowledge domain has the same shape:

| In code | In legal/corporate/medical |
|---------|---------------------------|
| Modules | Domains |
| Files | Document types |
| Symbols | Clauses / policies / procedures |
| Dependencies | References |
| Rules | Obligations |
| Invariants | Governing rules |
| Tiers | Authority ranking |

## The Category

Not a doc map. Not a search engine. Not RAG.

**A domain-aware knowledge graph with workflow-guided retrieval.**

Normal RAG: chunk documents, embed, retrieve by similarity, hope for the best.
This approach: structure first, taxonomy first, authority first, workflow first, retrieval second.

## Five Layers

1. **Corpus** — Raw materials (PDFs, policies, SOPs, contracts, handbooks, regulations)
2. **Taxonomy** — Classification (domain, subdomain, case type, risk level, jurisdiction)
3. **Knowledge Map** — Structural graph (this procedure depends on this regulation, this policy supersedes that one)
4. **Workflow** — Operating paths (when X happens: read these, ignore these, escalate here, use this template)
5. **Governance** — Trust (source authority, version, effective date, jurisdiction, owner, audit trail, confidence)

## The Killer Feature

Case-aware guided context packs. For any incoming task, build:

- Domain classification
- Relevant authoritative docs
- Mandatory reading order
- Active rules and exceptions
- Required inputs still missing
- Recommended templates
- Escalation route
- Confidence and source trace

## Example Domains

**Legal:** Practice area → jurisdiction → procedure type → governing laws → case templates → exception pathways → deadline rules

**University:** Admissions, conduct, disability, faculty, grants, procurement, ethics. Which policy governs what, which office owns each process, what approval chain applies.

**Manufacturing:** Production lines → safety SOPs → maintenance → quality control → incident protocols → supplier standards → compliance. When a defect occurs: what SOP governs it, what checklist applies, who to notify.

## Universal Entity Model

Works for code and non-code:

```
Corpus, Domain, Subdomain, Artifact, ArtifactType,
AuthorityLevel, Owner, Version, EffectiveDate,
Jurisdiction/Scope, Topic, WorkflowStep, DecisionPoint,
Exception, RequiredInput, Output/Template, Dependency,
Citation, Invariant, RiskLevel
```

## Critical Design Principles

- Source-grounded guide first, answer second
- Authority ranking is non-negotiable (especially legal, medical)
- Explicit uncertainty and confidence levels
- Human review in the loop for high-stakes domains
- "Not legal advice / not medical diagnosis" boundaries
- Auditability and traceability

## The Three-Layer Stack

```
Layer A — LDF:           Knowledge hardening, invariants, human/AI workflow
Layer B — Lifeblood:     Graph extraction, rules, dependencies, evidence
Layer C — Domain Packs:  Legal, corporate, medical, university, factory
```

## Execution Path

```
Now:       Lifeblood for code. Prove it. Get users.
+6 months: Extract generic core (graph + rules + evidence).
Then:      Knowledge Architecture track with domain packs.
```

## Ratings (from chat agent review, 2026-04-07)

- Originality: 9.4/10
- Real-world usefulness: 9.6/10
- Scalability across industries: 9.7/10
- Legacy potential: 9.8/10
- Risk if done too loosely: 9.2/10
