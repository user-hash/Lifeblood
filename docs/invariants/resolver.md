# Identifier Resolution Invariants

Every read-side MCP tool that takes a `symbolId` parameter routes
through `Lifeblood.Application.Ports.Right.ISymbolResolver` before doing
graph or workspace lookups. The resolver is the single source of truth
for "what does this user-supplied identifier mean". It canonicalizes
truncated method IDs, resolves bare short names, and computes the merged
read model for partial types.

- **INV-RESOLVER-001. Identifier resolution is a port.** Every read-side tool
  that takes a `symbolId` parameter (`lifeblood_lookup`, `lifeblood_dependants`,
  `lifeblood_dependencies`, `lifeblood_blast_radius`, `lifeblood_file_impact`,
  `lifeblood_find_references`, `lifeblood_find_definition`,
  `lifeblood_find_implementations`, `lifeblood_documentation`, `lifeblood_rename`)
  routes through `ISymbolResolver` before any graph or workspace lookup. NEVER
  add a read-side tool that calls `graph.GetSymbol` or `graph.GetIncomingEdgeIndexes`
  directly with the user's raw input.

- **INV-RESOLVER-002. The resolver accepts every input format.** Resolution
  order: exact canonical match → truncated method form (`method:NS.Type.Name`
  with no parens, lenient single-overload match) → bare short name (no kind
  prefix and no namespace, looks up the short-name index) → **extracted
  short name from a kind-prefixed or namespaced input** (see INV-RESOLVER-005).
  Returns `SymbolResolutionResult` with `Outcome`, `CanonicalId`, `Symbol`,
  `PrimaryFilePath`, `DeclarationFilePaths`, `Candidates`, and `Diagnostic`.

- **INV-RESOLVER-003. Partial-type unification is a read model.** The graph
  stores raw symbols (one per partial declaration; last-write-wins remains the
  storage policy in `GraphBuilder.AddSymbol`). The resolver walks the type's
  incoming `EdgeKind.Contains` edges from `SymbolKind.File` symbols to discover
  every partial declaration file at read time. Schema unchanged —
  `Lifeblood.Domain.Graph.Symbol.FilePath` stays a single value. The merged
  view lives entirely on `SymbolResolutionResult.PrimaryFilePath` +
  `SymbolResolutionResult.DeclarationFilePaths`.

- **INV-RESOLVER-004. Primary file path for partial types is deterministic.**
  Picker rules in `LifebloodSymbolResolver.ChoosePrimaryFilePath`:
  (1) filename matches the type name exactly, (2) filename starts with
  `"<TypeName>."` (shortest match wins among prefix matches), (3) lexicographic
  first as final fallback. Same input + same graph → same primary, always.

- **INV-RESOLVER-005. Wrong-namespace inputs resolve via the trailing short-name segment.**

- **INV-RESOLVER-006. Kind correction inside the truncated-method path.** When the user supplies a `method:NS.Type.X` id pointing at a real type that carries no method named `X`, but the same type has a Property / Field / Event named `X`, the resolver returns that member with `ResolveOutcome.KindCorrectedOnContainingType` and a diagnostic explaining the correction. Type-scoped kind correction takes precedence over the global short-name fallback (Rule 4 / `ShortNameFromQualifiedInput`) because the user already committed to a namespace; the more specific resolution is the honest answer. Method-by-that-name still wins over a same-named property — kind correction only fires when zero method overloads exist. Pinned by `SymbolResolverTests.Resolve_MethodPrefix_OnPropertyOnSameType_KindCorrected`, `Resolve_MethodPrefix_OnFieldOnSameType_KindCorrected`, `Resolve_MethodPrefix_PrefersMethodOverProperty_WhenBothPresent`. Closes LB-BUG-002.

- **INV-RESOLVER-007. Rule 4 short-name fallback never crosses containing types or kinds for member-kind inputs.**
