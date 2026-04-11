# Tools

Lifeblood exposes **22 MCP tools** in one session. 12 read, 10 write. All share the same loaded workspace.

Every read-side tool that takes a `symbolId` routes through `ISymbolResolver` before any graph or workspace lookup. Resolution order: exact canonical match, then truncated method form (single-overload lenient), then bare short name, then **extracted short name from a kind-prefixed or qualified input** (new in v0.6.3, `INV-RESOLVER-005`; wrong-namespace typos still resolve when the trailing segment is unique). Truncated ids, bare short names, and qualified-but-wrong-namespace ids all resolve correctly across the whole read surface.

## Write-side (compiler-as-a-service)

## Read-side (semantic intelligence)

The difference: the AI agent doesn't guess what your code does. It **asks the compiler**.

---

## ¹ `lifeblood_dead_code` known limitations (`INV-DEADCODE-001`, v0.6.3)

`lifeblood_dead_code` ships as **experimental / advisory** in v0.6.3. Every response carries `status: "experimental"` and a `warning` field listing the three known false-positive classes so agents cannot consume findings without seeing the caveat.

**Known false-positive classes:**

1. **Method-group references.** A private method passed as a delegate (`new Lazy<T>(Load)`, event handler registration, LINQ `Where(predicate)`) never produces a direct call-site `InvocationExpressionSyntax`, so the edge extractor does not emit a `Calls` edge into the referenced method. Example: `Lifeblood.Adapters.CSharp.Internal.BclReferenceLoader.Load` is flagged as dead because its only caller is `new Lazy<>(Load)` in the same type's field initializer.

3. **Same-class private field reads.** Private fields (`_fs`, `_nuget`, `_refCache`) accessed only from sibling methods on the same type are flagged because the extractor does not emit read-edges at method→field granularity.

**Consumer guidance:**

- Treat every finding as a **candidate** to verify, not a fact.
- Cross-check with `lifeblood_find_references` (which has the same drift class for #2 but not for #1 or #3).
- Inspect the source directly before deleting any "dead" symbol.
- On real multi-module Unity workspaces expect ~20-25% of findings to be false positives; the tool is still useful for surfacing *candidates* but the agent should reason about each one.

See [CLAUDE.md § INV-DEADCODE-001](../CLAUDE.md) for the full architectural rationale, the specific extractor code paths involved, the regression tests that pin the synthetic happy-path, and the v0.6.4 follow-up scope.

---

## Symbol ID format

Tools that take a `symbolId` use this format:
- `type:Namespace.TypeName`
- `method:Namespace.TypeName.MethodName(ParamType)`
- `field:Namespace.TypeName.FieldName`
- `property:Namespace.TypeName.PropertyName`
- `property:Namespace.TypeName.this[ParamType]` (indexer)
- `mod:AssemblyName`
- `file:relative/path/to/File.cs`
- `ns:Namespace`

Lifeblood owns the parameter-type display format for method IDs via `Internal.CanonicalSymbolFormat`. Every method-ID builder in the C# adapter routes through it, so the symbol ID grammar does not silently drift with Roslyn version changes (`INV-CANONICAL-001`).

If you don't know the canonical id, ask `lifeblood_resolve_short_name MyType` and use the returned `symbolId`. The resolver also accepts:
- Truncated method ids like `method:Namespace.TypeName.MethodName` when there is exactly one matching overload (`LenientMethodOverload`)
- Bare short names with no kind prefix and no namespace (`ShortNameUnique`)

- **Kind-prefixed ids with wrong namespace** (`ShortNameFromQualifiedInput`, v0.6.3 / `INV-RESOLVER-005`): when the exact / truncated / bare paths all fail, the resolver extracts the trailing short-name segment and looks it up in the short-name index. If that produces exactly one hit, the resolver silently corrects the namespace and returns the real canonical id with a diagnostic explaining the correction.

## Incremental Re-Analyze

After the first `lifeblood_analyze`, subsequent calls with `incremental: true` only recompile modules whose source files changed since the last analysis. File changes are detected via filesystem timestamps, and csproj timestamp changes (`INV-BCL-005`) also trigger per-module re-discovery so a `<Nullable>` or `<AllowUnsafeBlocks>` toggle doesn't leave stale compilation facts behind.

```
lifeblood_analyze projectPath="/my/project"                    → full analysis (~14-34 s depending on workspace size)
lifeblood_analyze projectPath="/my/project" incremental=true   → seconds when nothing changed, else re-analyze only the dirty modules
```

Module additions/removals automatically fall back to full re-analyze.

## Workspace auto-refresh for compile-check

`lifeblood_compile_check` auto-refreshes the workspace when any tracked file has changed on disk since the last analyze, so you can edit source between an analysis and a compile-check without stale results. Opt out with `staleRefresh: false` to check against the pinned state. The response carries `autoRefreshed: true` + `changedFileCount: N` when a refresh actually ran.
