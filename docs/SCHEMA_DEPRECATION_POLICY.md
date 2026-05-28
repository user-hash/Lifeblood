# Schema deprecation policy

How Lifeblood evolves its public wire contracts (MCP tool input / output
shapes, the response envelope, the graph JSON schema) without breaking
external integrators silently.

Tracked as **INV-WIRE-CONTRACT-001** (canonical pin in
`docs/invariants/mcp-protocol.md` once a future commit lifts this policy
into the invariant tree; the policy file itself stays as the
human-readable rulebook).

## What counts as a "wire contract"

| Surface | Owned by | Pinned by |
|---------|----------|-----------|
| `ResponseEnvelope` field set | `Lifeblood.Domain.Results.ResponseEnvelope` | `ResponseEnvelopeWireShapeContractTests` |
| MCP tool input schemas (`tools/list` `inputSchema` field) | `ToolRegistry.GetTools` | `ToolSchemaSnapshotTests` |
| MCP tool output shapes (the JSON each handler returns) | per-tool, in `ToolHandler.Handle*` | per-tool tests |
| Graph JSON schema | `schemas/graph.schema.json` | `JsonGraphRoundTripTests` |

## Versioning

Lifeblood ships one stable contract version at a time. The current
version is **v1** (the first version frozen — every release on `main`
before this policy implicitly shipped v1-shaped responses; the
ratchets pin the shape so it does not drift accidentally).

When a contract must change in a breaking way, the new version is
**v2**, and v1 stays supported for at least one full minor release
under the deprecation rules below.

## Compatible changes (non-breaking, no version bump)

These changes are always allowed:

1. **Adding a new optional output field.** Existing clients that read
   only the v1 field set keep working.
2. **Adding a new optional input parameter.** Existing clients that
   don't supply it keep working as long as the default behavior matches
   pre-change.
3. **Loosening a value constraint** (e.g. allowing `null` where
   previously a non-null value was required — only at the response
   level; tightening is breaking).
4. **Adding a new enum member** that is never emitted by default — only
   under an opt-in input flag. Clients that don't read the flag never
   see the new value.
5. **Adding a new MCP tool** to `tools/list`.
6. **Adding a new entry to the canonical parity diagnostic ID set**
   in `INV-DIAGNOSTIC-PARITY-001` (this is a regression-catch
   broadening, not a wire contract change).

Each addition still requires a snapshot-test update: the
field-set ratchet (`ResponseEnvelopeWireShapeContractTests`) and any
per-tool wire-shape test must be re-baselined to include the new field
so the next regression catches a quiet removal.

## Breaking changes (require version bump + deprecation window)

These require explicit major bookkeeping:

1. **Renaming a field.** v1 ships the old name; v2 ships the new name;
   both names are emitted for the deprecation window.
2. **Removing a field.** v1 keeps the field; v2 drops it. The field is
   marked `obsolete: true` on `tools/list` for the window.
3. **Changing a field's type** (e.g. `Int32 → Int64`, `string →
   string[]`).
4. **Removing or renaming an MCP tool.**
5. **Removing or renaming an enum member.**
6. **Tightening a value constraint** (requiring non-null where a
   nullable was accepted, narrowing an integer range).
7. **Changing the canonical id format** in `INV-CANONICAL-001`.

### Deprecation window

When a breaking change is needed:

1. The new schema (`v2`) ships in the same release as v1 — both are
   served simultaneously. v1 clients keep getting v1 responses; v2
   clients opt in via `mcp-protocol-version: v2` in the initialization
   handshake (TBD when v2 actually lands; this policy reserves the
   mechanism).
2. v1 stays supported for at least **one full minor release** after v2
   ships. During the window, v1 responses include a non-fatal
   `Limitations[]` entry naming the v2 successor (`"This response uses
   schema v1. v2 is available with mcp-protocol-version: v2. v1 will be
   removed in v0.X.Y."`).
3. Removal is announced in the **CHANGELOG** of the release that
   drops v1 support. The "Removed" section names every field / tool /
   enum value that left v1.

## Snapshot files

Versioned schema snapshots live under `schemas/`:

- `schemas/graph.schema.json` — graph JSON.
- `schemas/tools/v1/<tool>.schema.json` — per-tool MCP input schemas
  as exposed by `tools/list` through `ToolRegistry.GetDefinitions()`.
  `ToolSchemaSnapshotTests` fails if a registered tool is missing a
  snapshot, if a snapshot drifts from the registry schema, or if the
  directory carries a stale snapshot for a removed/renamed tool.
- Per-tool output shapes still live in focused tests beside the handler
  behavior they exercise. Promote them to versioned files when a tool's
  output contract needs file-backed schema enforcement.

The contract ratchets read either the snapshot file or the reflection
of the current type. They MUST fail loudly on drift. Updating a
snapshot to match a breaking change without bumping the schema version
is itself a contract violation — the ratchet exists exactly to prevent
that.

## Invariant ratchets and this policy

Lifeblood ships every contract as a typed INV in `docs/invariants/`
with a pinned test. The deprecation policy applies to anything an
external integrator can observe at the wire — not to internal
invariants that have no external surface. If an INV pin is only
verifiable by reading Lifeblood source code, this policy does not
apply; if it's pinned to a JSON field name, an MCP tool name, an enum
member, a canonical id string, or a publicly-documented behavior, it
falls under the rules above.

When in doubt, treat the change as breaking and follow the deprecation
window. The cost of a needless version bump is one extra atom in the
release; the cost of an unannounced break is integrators silently
abandoning the tool.
