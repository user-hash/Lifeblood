# Interpreting impact and freshness

## Blast radius

`lifeblood_blast_radius` reports distinct symbols reachable through incoming
dependency edges, up to `maxDepth` (default 10). Its count describes possible
transitive impact. It is not a count of broken files or failing tests. A central
application type can connect many otherwise separate features, making that
transitive set large.

Start with `maxDepth:1` for immediate dependants. Use `groupBy:"both"` to inspect
the path buckets and owning modules, and compare `directDependants` with
`affectedCount`. `previewPerGroup` limits returned examples; it does not limit
the counts. Production, Test, Editor, Generated and Vendored are distinct
path buckets. Grouping does not change traversal or discard central types.

## Which tests touch a type

```json
{"target":"type:Example.Service"}
```

Pass that argument to `lifeblood_test_impact`. It expands a type to its directly
owned members and follows incoming dependency edges. Each `affectedTestClasses`
entry includes `filePath`, `testMethodNames`, `minDistance` and a direct,
one-hop or transitive classification. `recommendedFilters` supplies class
filters for `dotnet test`. The input name is `target`; a source file path also
works.

This is static test-impact evidence. It does not establish executed coverage,
assertion quality or a passing test run. Reflection heuristics are optional and
reported separately. Partial test classes have one class-level primary path;
inspect source references for declarations spread across several files.

## Freshness

Ordinary MCP responses perform a bounded mtime/existence observation of up to
256 parsed source paths. On a larger publication, `freshness.status:"incomplete"`
and the checked/unchecked counts describe that sampling limit. Repeated
incomplete status alone is not evidence that sources changed.

`driftObserved` reports a newer or missing source in that sample. Zero observed
changes does not establish content equality, including when every path was
checked: mtimes do not compare source bytes or recapture project membership.

For a decision requiring current input identity, call `lifeblood_snapshots` with
`action:"list", checkDrift:true` and inspect its canonical per-snapshot drift
receipt. Refresh changed inputs before relying on them. Ordinary query envelopes
remain bounded observations; checking drift does not change their meaning.
