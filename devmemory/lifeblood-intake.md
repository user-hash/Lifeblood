# Lifeblood product intake

Unstarted product requests: new findings land here first. When work begins, promote the item to [the implementation backlog](lifeblood-tracking.md), then remove it from intake.

## LB-INTAKE-20261111-001 - Consumer follow-up triage

Type: Improvement
Priority: Unprioritized
Source: Product backlog
Workspace: Lifeblood

What:
Separate reproducible product gaps from client integration failures and requests already covered by existing tools.

Why it matters:
Avoid duplicate facilities and unsupported closure claims.

Fix shape:
Prioritize remaining product requests using synthetic or explicitly public reproduction cases.

## LB-INTAKE-20261111-003 - Descriptor-scoped refresh

Type: Improvement
Priority: Unprioritized
Source: Product backlog
Workspace: Lifeblood

What:
Descriptor additions and removals can require full analysis even when a caller needs one unchanged owner module.

Why it matters:
Broad refresh can dominate an otherwise bounded edit workflow.

Fix shape:
Measure dependency-qualified descriptor refresh within the existing analyzer and immutable publication model.

## LB-INTAKE-20260718-047 - Ledger type vocabulary

Type: Docs
Priority: Low
Source: Product backlog
Workspace: Lifeblood

What:
Ledger entry types need one accepted vocabulary.

Why it matters:
Shape checks currently require metadata without validating every type value.

Fix shape:
Use one shared vocabulary when validating intake and implementation entries.

## LB-INTAKE-20260802-049 - Test recommendations since a trusted baseline

Type: Improvement
Priority: High
Source: Product backlog
Workspace: Lifeblood

What:
Test impact currently targets individual files or symbols rather than the complete change set since a supplied test receipt.

Why it matters:
An older passing receipt does not validate subsequent changes.

Fix shape:
Join bounded Git changes with semantic test impact and explicit baseline, drift and unsupported-relationship evidence.

## LB-INTAKE-20260802-050 - Composable project review

Type: UX
Priority: Medium
Source: Product backlog
Workspace: Lifeblood

What:
A review needs publication, invariant, source-control and external-test evidence from separate tools.

Why it matters:
Mixed identities or stale receipts can overstate confidence.

Fix shape:
Compose existing bounded receipts under one publication without a global quality score or inferred test pass.

## LB-INTAKE-20260930-061 - SDK source exclusions

Type: Bug
Priority: Medium
Source: Product backlog
Workspace: Lifeblood

What:
Source discovery does not fully evaluate SDK Compile Remove items.

Why it matters:
A source file may be attributed to a project that excludes it.

Fix shape:
Define and test supported source-item admission in the existing project discovery authority.

## LB-INTAKE-20260930-060 - Linked-source rename edits

Type: Bug
Priority: Medium
Source: Product backlog
Workspace: Lifeblood

What:
Multiple documents can project identical rename edits for one physical source occurrence.

Why it matters:
Applying duplicate edits can shift later spans.

Fix shape:
Deduplicate identical physical edits, reject conflicting replacements and retain provenance.
