# Post-BCL Architectural Fixes — Plan v4 (three-seam framing)

**Status:** APPROVED with corrections from external review (2026-04-10).
Corrections 1 and 2 folded into the wording below; corrections 3 and 4 already
shipped in v2 (acknowledged inline). Awaiting Matic's final "go" before
implementation.

**External review verdict (2026-04-10):**
> Approve the plan after these corrections. Architecturally, this is the
> cleanest version so far. Keep it as one v4 plan file with three seam
> sections — the three seams are related enough that splitting them would
> scatter the core reasoning.

**The four corrections:**
1. ✅ **Folded** — Collapse `ResolveResult` + `MergedSymbol` into one
   `SymbolResolutionResult` DTO. Don't return raw `Symbol` with retrofitted
   `FilePaths[]`; the merged file list belongs on the resolution result, not
   on the domain Symbol. Preserves domain purity. (See §2.1.)
2. ✅ **Folded** — `find_references includeDeclarations` is a write-side
   operation policy, not just an identifier-resolution issue. Modeled as an
   explicit parameter on `RoslynCompilationHost.FindReferences`, with the
   resolver providing the canonical-ID plumbing underneath. (See §2.6.)
3. ⏭️ **Already done in current source** — Compilation creation is already
   extracted into `Lifeblood.Adapters.CSharp.Internal.ModuleCompilationBuilder.CreateCompilation`
   (the analyzer only orchestrates via `compilationBuilder.ProcessInOrder(...)`
   at `RoslynWorkspaceAnalyzer.cs:114`). No extraction step needed before
   establishing the convention. The convention sits on top of the existing
   builder.
4. ⏭️ **Already done in v2** — Csproj-edit invalidation shipped with the
   BCL fix as `AnalysisSnapshot.CsprojTimestamps` + the timestamp loop in
   `IncrementalAnalyze` (BCL plan §8, v2 rollout steps 6-8). Re-discovery
   on csproj edit rebuilds the entire `ModuleInfo` — every compilation fact
   is refreshed, not just BCL ownership. (Made explicit in §3 / `INV-COMPFACT-003`.)
**Author:** Claude (working with Matic)
**Date:** 2026-04-10
**Context:** Two reviewer reports against the running v2 BCL dist, plus
empirical verification of every claim. The five reported issues collapse into
**three missing architectural seams**, not five independent fixes.

---

## §0. The zoomed-out picture

| Reported finding | Underlying seam |
|---|---|
| **LB-BUG-002** truncated symbol ID returns `[]` | Seam #1 — Identifier resolution |
| **LB-BUG-004** partial type lookup non-deterministic | Seam #1 — same; partial-type unification is identifier resolution |
| **LB-FR-002** short-name resolver | Seam #1 — same; just another input format |
| **LB-FR-003** find_references should include declarations | Seam #1 — falls out once partials unify |
| **LB-BUG-005** allowUnsafeCode not propagated | Seam #2 — Csproj-driven compilation facts |
| (BCL ownership, already shipped in v2) | Seam #2 — first instance of the same pattern |
| **LB-BUG-003** execute Workspace global missing | Seam #3 — Adapter semantic view |

**Three architectural seams. Five reported findings as instances of those seams.**

The five-fix piecemeal version (v3 of this plan) was 26 sequential steps. The
three-seam version is 18 because each seam ships once and covers multiple
findings. More importantly, the three-seam version produces lasting architectural
contracts: future contributors don't need to re-derive the pattern when they
add the next compilation option, the next read-side tool, or the next consumer
of the loaded model.

## §1. Empirical evidence dump (so reviewers can verify everything)

All commands run against the running v2 dist (12:09 publish), DAWG project,
fresh full re-analyze:

```
lifeblood_analyze projectPath=D:/Projekti/DAWG incremental=false
  → Loaded: 44438 symbols, 86359 edges, 75 modules, 0 violations

# LB-BUG-001 — closed by v2 BCL fix
lifeblood_find_references method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch(Nebulae.BeatGrid.Audio.DSP.VoicePatch)
  → 18 refs including PatchPublisher.cs:106 voices[i].SetPatch(patch)

# LB-BUG-002 — NOT broken when full canonical ID is used
lifeblood_dependants method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch(Nebulae.BeatGrid.Audio.DSP.VoicePatch)
  → 8 callers including PatchPublisher.UpdateActiveVoices

# LB-BUG-002 — broken when reviewer's truncated form is used
lifeblood_dependants method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch        → []
lifeblood_lookup     method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch        → Symbol not found

# LB-BUG-003 — execute has no Workspace global
lifeblood_execute "return Workspace.CurrentSolution.Projects.Count();"
  → CS0103: The name 'Workspace' does not exist in the current context

# LB-BUG-004 — partial type lookup returns arbitrary partial
lifeblood_lookup type:Nebulae.BeatGrid.AdaptiveBeatGrid
  → filePath: Assets/_Project/Scripts/BeatGrid/TabVisualsRefresher.cs line 14
    (a tiny one-method partial; canonical home is AdaptiveBeatGrid.cs)

# LB-BUG-005 — Minis CS0227 false positive
lifeblood_diagnose moduleName=Minis
  → 2x CS0227 on MidiPort.cs:12 / MidiDeviceState.cs:13 + 1 unrelated CS1701

# Confirmation that Minis.csproj DOES declare AllowUnsafeBlocks
grep -E "AllowUnsafeBlocks" /d/Projekti/DAWG/Minis.csproj
  → <AllowUnsafeBlocks>True</AllowUnsafeBlocks>

# Confirmation that Lifeblood source NEVER reads it
grep -rn "AllowUnsafe\|WithAllowUnsafe" /d/Projekti/Lifeblood/src --include="*.cs"
  → (zero matches)

# Audio module (post-v2 BCL fix) — clean
lifeblood_diagnose moduleName=Nebulae.BeatGrid.Audio
  → 3 unused-field warnings, zero errors
```

Every claim in this plan is reproducible from these commands.

---

## §2. Seam #1 — `ISymbolResolver` (identifier resolution as a port)

### The architectural problem

Lifeblood has **five different identifier-resolution policies** today, each
embedded in a different read-side surface:

| Surface | Policy |
|---|---|
| `lookup` | `graph.GetSymbol(id)` — exact dictionary match, no leniency |
| `dependants` | `graph.GetIncomingEdgeIndexes(id)` — exact dictionary match, no leniency |
| `dependencies` | same as `dependants` |
| `blast_radius` | reads graph by exact id |
| `file_impact` | reads graph by exact id |
| `find_references` | `RoslynCompilationHost.FindReferences` → `ResolveFromSource` → `RoslynWorkspaceManager.FindInCompilation` — has lenient single-overload escape valve, bypasses the graph entirely |
| `find_definition` | same as `find_references` |
| Graph storage of partial types | `GraphBuilder.AddSymbol` — last-write-wins, non-deterministic primary file |

Five policies, three input formats users actually try (canonical full ID,
truncated method, bare short name), one undocumented merge-or-overwrite
behavior for partial types. Every reported finding in this layer
(BUG-002, BUG-004, FR-002, FR-003) is a direct consequence of this fragmentation.

The reviewer's BUG-002 is the cleanest illustration: they sent
`method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch` (no parens) to
`dependants`. The graph stores it as
`method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch(Nebulae.BeatGrid.Audio.DSP.VoicePatch)`
(canonical, with param sig). Pure dict lookup → empty. The user saw
"`find_references` works with this same input but `dependants` doesn't" and
concluded "different code paths" — but the actual root cause is "two of the
five policies happen to be lenient and three aren't".

### The architectural answer

**One resolver port. Every read-side tool routes through it. The resolver is
the single place that knows how to interpret user-supplied identifiers.**

```
                ┌────────────────────────────────────────┐
                │ MCP read-side handlers                 │
                │  HandleLookup, HandleDependants,       │
                │  HandleDependencies, HandleBlastRadius,│
                │  HandleFileImpact, HandleResolveShort  │
                └────────────────────┬───────────────────┘
                                     │
                                     ▼
                ┌────────────────────────────────────────┐
                │ ISymbolResolver  (Application port)    │
                │   Resolve(graph, userInput) → canonId? │
                │   ResolveShortName(graph, name) → ids[]│
                │   LookupMerged(graph, id) → MergedSymbol│
                └────────────────────┬───────────────────┘
                                     │
                                     ▼
                ┌────────────────────────────────────────┐
                │ LifebloodSymbolResolver  (Connector)   │
                │  1. Exact canonical match              │
                │  2. Truncated method → lenient overload│
                │  3. Bare short name → ShortNameIndex   │
                │  4. Partial type unification (merge    │
                │     all per-partial Symbol records into│
                │     one logical view with FilePaths[]) │
                └────────────────────────────────────────┘
```

**Critical design decision: the graph stays raw.** The graph builder continues
to store one `Symbol` record per partial declaration (last-write-wins remains
the storage policy). The resolver does the merging on read. This avoids a
schema change to `Symbol` — `Symbol.FilePath` stays a single value, and the
merged view is exposed via a separate `MergedSymbol` record returned by the
resolver. Existing consumers of `Symbol.FilePath` don't break; tools that want
the merged view ask the resolver.

This is the **inverse** of what v3 of this plan proposed (which added a
`Symbol.FilePaths[]` schema field). The resolver-on-read approach is cleaner
because:
- The graph remains a passive storage layer (single responsibility).
- Schema is unchanged → no risk to existing JSON serialization, golden repo
  tests, or external graph consumers.
- The merge policy can evolve over time (filename match → prefix match →
  member count → lexicographic) without rewriting graph data.
- The resolver is the only thing that needs unit-test coverage for the merge
  rules.

### Schema additions (lightweight)

#### 2.1 `Lifeblood.Application/Ports/Right/ISymbolResolver.cs` (NEW)

Per **Correction 1** from the external review: there is ONE result DTO,
`SymbolResolutionResult`, NOT two (no separate `ResolveResult` + `MergedSymbol`
split). The resolver returns the canonical ID, the resolution outcome, the
referenced raw `Symbol` (read-only handle into the graph), the candidate list
(populated on ambiguity), the diagnostic string, and the merged
`DeclarationFilePaths` for partial types. **No retrofitted field on
`Symbol`** — partial unification is a read model on the resolution result,
not a graph schema change.

```csharp
namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Single resolver for user-supplied symbol identifiers. Every read-side tool
/// (lookup, dependants, dependencies, blast_radius, file_impact, etc.) MUST
/// route through this resolver before doing graph lookups.
///
/// See INV-RESOLVER-001..004 in CLAUDE.md.
/// </summary>
public interface ISymbolResolver
{
    /// <summary>
    /// Resolve a user-supplied identifier to a canonical symbol ID and the
    /// associated merged read-model. Resolution order:
    ///   1. Exact canonical match (fast path).
    ///   2. Truncated method form: "method:NS.Type.Name" with no parens →
    ///      lenient single-overload match.
    ///   3. Bare short name: no kind prefix and no namespace → short-name index.
    ///   4. Not found / ambiguous: returns NotFound or Ambiguous* outcomes
    ///      with Candidates and a Diagnostic populated.
    ///
    /// For partial types, the result's <see cref="SymbolResolutionResult.DeclarationFilePaths"/>
    /// is populated with every partial declaration file (sorted lexicographically),
    /// with <see cref="SymbolResolutionResult.PrimaryFilePath"/> chosen deterministically
    /// per INV-RESOLVER-004.
    /// </summary>
    SymbolResolutionResult Resolve(SemanticGraph graph, string userInput);

    /// <summary>
    /// Resolve a short name (no namespace) to all matching canonical IDs.
    /// Used by the standalone lifeblood_resolve_short_name tool and as a
    /// fallback inside Resolve.
    /// </summary>
    ShortNameMatch[] ResolveShortName(SemanticGraph graph, string shortName);
}

/// <summary>
/// Single result DTO for identifier resolution. Combines canonicalization
/// (CanonicalId), the resolution outcome, the referenced graph Symbol,
/// the partial-type read model (DeclarationFilePaths + PrimaryFilePath),
/// and the diagnostic-on-miss surface (Candidates + Diagnostic).
///
/// IMPORTANT — partial-type unification lives HERE, not on Symbol.
/// The graph stores raw symbols (one per partial declaration; last-write-wins
/// remains the storage policy). DeclarationFilePaths is computed by the
/// resolver from the existing file→type Contains edges in the graph.
/// </summary>
public sealed class SymbolResolutionResult
{
    /// <summary>
    /// The canonical symbol ID, or null if not resolved.
    /// When non-null, guaranteed to be a key in <c>graph.GetSymbol</c>.
    /// </summary>
    public string? CanonicalId { get; init; }

    /// <summary>What rule the resolver applied. NotFound and Ambiguous*
    /// outcomes have CanonicalId == null and Candidates populated.</summary>
    public ResolveOutcome Outcome { get; init; }

    /// <summary>
    /// The raw graph Symbol record for the resolved ID, or null on miss.
    /// This is a HANDLE into graph storage — read-only, no copy. For
    /// partial types, this is whichever partial GraphBuilder happened to
    /// store last (the resolver does not mutate it). The merged view lives
    /// in <see cref="PrimaryFilePath"/> and <see cref="DeclarationFilePaths"/>,
    /// both computed by the resolver from the graph's Contains edges.
    /// </summary>
    public Symbol? Symbol { get; init; }

    /// <summary>
    /// Deterministic primary file path for the resolved symbol. For partial
    /// types, picked per INV-RESOLVER-004: filename matches type name →
    /// filename starts with "&lt;TypeName&gt;." (shortest first) →
    /// lexicographic first. For non-partial symbols, equals the symbol's
    /// only file. Empty string when CanonicalId is null.
    /// </summary>
    public string PrimaryFilePath { get; init; } = "";

    /// <summary>
    /// All declaration files for the resolved symbol. For partial types,
    /// every file containing a partial declaration, sorted lexicographically.
    /// For non-partial symbols, exactly one entry. Empty array when
    /// CanonicalId is null.
    /// </summary>
    public string[] DeclarationFilePaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Candidate canonical IDs when Outcome is Ambiguous* — for example,
    /// a short name that matches multiple types in different namespaces,
    /// or a truncated method ID with multiple overloads.
    /// </summary>
    public string[] Candidates { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Human-readable diagnostic explaining the outcome. For NotFound:
    /// "Symbol not found: <input>. Tried exact match, lenient method
    /// overload, and short-name lookup. Did you mean: A, B, C?" Closes
    /// the diagnostic-on-miss feature request from the original backlog.
    /// </summary>
    public string? Diagnostic { get; init; }
}

public enum ResolveOutcome
{
    ExactMatch,
    LenientMethodOverload,
    ShortNameUnique,
    NotFound,
    AmbiguousShortName,
    AmbiguousMethodOverload,
}

public sealed class ShortNameMatch
{
    public string CanonicalId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Kind { get; init; } = "";
}
```

Note the deliberate **absence** of any change to `Lifeblood.Domain.Graph.Symbol`.
The domain layer is untouched. The merged view lives entirely on the
application-port resolution result. Per the reviewer:
> "preserves domain purity and keeps partial-type unification as a read model,
> not a graph-schema distortion."

#### 2.2 `Lifeblood.Domain.Graph.SemanticGraph` — short-name index

`SemanticGraph` already has a lazy-built `GraphIndexes` (`SemanticGraph.cs`,
private class line 115). Add a third index:

```csharp
private sealed class GraphIndexes(
    Dictionary<string, Symbol> symbolById,
    Dictionary<string, List<int>> outgoing,
    Dictionary<string, List<int>> incoming,
    Dictionary<string, List<Symbol>> symbolsByShortName)         // ← new
{
    // ...
    public Dictionary<string, List<Symbol>> SymbolsByShortName { get; }
        = symbolsByShortName;
}

// In BuildIndexes(), populate the new index alongside the existing two.
// Public accessor:
public IReadOnlyList<Symbol> FindByShortName(string name) { ... }
```

This is the only change to the Domain layer. It's a read-side index,
not a schema change — `Symbol` itself is unchanged.

#### 2.3 `Lifeblood.Connectors.Mcp.LifebloodSymbolResolver` (NEW)

Concrete implementation of `ISymbolResolver`. Does the resolution-order
walk and the partial-type merge inside `Resolve` itself — there is no
separate `LookupMerged` method, because the merged read model is part of
the single `SymbolResolutionResult` returned by `Resolve`. ~180 lines
including merge rules.

The merge logic walks the EXISTING file→type Contains edges in the graph
to discover all partial declaration files. **Zero schema change** — the
graph already has every partial declaration as a `file:...` symbol with a
Contains edge to the type, so the resolver just walks those edges.

```csharp
private SymbolResolutionResult BuildResolved(
    SemanticGraph graph,
    string canonicalId,
    Symbol primary,
    ResolveOutcome outcome)
{
    // For non-type symbols, the merged view degenerates to a single file.
    if (primary.Kind != SymbolKind.Type)
    {
        return new SymbolResolutionResult
        {
            CanonicalId = canonicalId,
            Outcome = outcome,
            Symbol = primary,
            PrimaryFilePath = primary.FilePath,
            DeclarationFilePaths = string.IsNullOrEmpty(primary.FilePath)
                ? Array.Empty<string>()
                : new[] { primary.FilePath },
        };
    }

    // Partial-type unification: walk incoming Contains edges from File
    // symbols. Every partial declaration of a type produces a
    // `file:X Contains type:Y` edge during graph build (even when
    // GraphBuilder dedups the type symbol via last-write-wins).
    var allFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (int idx in graph.GetIncomingEdgeIndexes(canonicalId))
    {
        var edge = graph.Edges[idx];
        if (edge.Kind != EdgeKind.Contains) continue;
        var fileSymbol = graph.GetSymbol(edge.SourceId);
        if (fileSymbol?.Kind != SymbolKind.File) continue;
        if (!string.IsNullOrEmpty(fileSymbol.FilePath))
            allFilePaths.Add(fileSymbol.FilePath);
    }

    // Backstop: if no Contains edges led to file symbols (rare, possibly
    // a malformed graph), fall back to whatever the symbol record itself
    // recorded. Also belt-and-braces for the non-partial case.
    if (allFilePaths.Count == 0 && !string.IsNullOrEmpty(primary.FilePath))
        allFilePaths.Add(primary.FilePath);

    var sortedPaths = allFilePaths
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var primaryPath = sortedPaths.Length > 0
        ? ChoosePrimaryFilePath(primary.Name, sortedPaths)
        : "";

    return new SymbolResolutionResult
    {
        CanonicalId = canonicalId,
        Outcome = outcome,
        Symbol = primary,
        PrimaryFilePath = primaryPath,
        DeclarationFilePaths = sortedPaths,
    };
}

private static string ChoosePrimaryFilePath(string typeName, string[] filePaths)
{
    // Rule 1: filename matches the type name exactly.
    var nameMatch = filePaths.FirstOrDefault(p =>
        string.Equals(Path.GetFileNameWithoutExtension(p), typeName,
            StringComparison.OrdinalIgnoreCase));
    if (nameMatch != null) return nameMatch;

    // Rule 2: filename starts with "<typeName>." (the "Foo.Init.cs" partial
    // naming convention DAWG and many Unity projects use). Pick the shortest
    // match — bare "Foo.cs" beats "Foo.Init.cs" beats "Foo.Init.Audio.cs".
    var prefixMatches = filePaths
        .Where(p => Path.GetFileNameWithoutExtension(p)
            .StartsWith(typeName + ".", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => Path.GetFileName(p).Length)
        .ToArray();
    if (prefixMatches.Length > 0) return prefixMatches[0];

    // Rule 3: deterministic fallback — lexicographic first.
    return filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
}
```

**Important**: this approach reuses the EXISTING `file:X Contains type:Y`
edges already in the graph. We do NOT add a new schema field. The graph
already has every partial declaration as a `file:...` symbol with a Contains
edge to the type — the resolver just walks those edges to discover all the
files. **Zero schema change.**

#### 2.4 MCP routing — every read-side handler routes through the resolver

In `Lifeblood.Server.Mcp/ToolHandler.cs`, each `Handle*` that takes a
`symbolId` parameter resolves first. The resolver returns ONE DTO that
carries everything the handler needs:

```csharp
private McpToolResult HandleLookup(JsonElement? args)
{
    if (!_session.IsLoaded) return ErrorResult("...");
    var raw = WriteToolHandler.GetString(args, "symbolId");
    if (string.IsNullOrEmpty(raw)) return ErrorResult("symbolId is required");

    var resolved = _resolver.Resolve(_session.Graph!, raw);
    if (resolved.CanonicalId == null)
        return ErrorResult(resolved.Diagnostic
            ?? $"Symbol not found: {raw}");

    // resolved.Symbol is a handle into graph storage; resolved.PrimaryFilePath
    // and resolved.DeclarationFilePaths are the merged read model for
    // partial types. No second resolver call needed.
    var sym = resolved.Symbol!;
    var result = new
    {
        sym.Id,
        sym.Name,
        sym.QualifiedName,
        Kind = sym.Kind.ToString(),
        FilePath = resolved.PrimaryFilePath,                  // deterministic primary
        FilePaths = resolved.DeclarationFilePaths,            // all partials, sorted
        sym.Line,
        sym.ParentId,
        Visibility = sym.Visibility.ToString(),
        sym.IsAbstract,
        sym.IsStatic,
        sym.Properties,
    };
    return TextResult(JsonSerializer.Serialize(result, JsonOpts));
}
```

Same single-DTO pattern for `HandleDependants`, `HandleDependencies`,
`HandleBlastRadius`, `HandleFileImpact`. Each handler resolves once, then
does its existing graph query against `resolved.CanonicalId`.

`find_references` and `find_definition` route through the same resolver
too, but they retain their existing live-walker code path because the
workspace-manager lookup is needed for the metadata-symbol case. The
resolver becomes the FIRST step (parse the input → canonical ID), and
the workspace manager becomes the SECOND step (find the live Roslyn
symbol from the canonical ID).

#### 2.5 New tool — `lifeblood_resolve_short_name`

```csharp
private McpToolResult HandleResolveShortName(JsonElement? args)
{
    if (!_session.IsLoaded) return ErrorResult("...");
    var name = WriteToolHandler.GetString(args, "name");
    if (string.IsNullOrEmpty(name)) return ErrorResult("name is required");

    var matches = _resolver.ResolveShortName(_session.Graph!, name);
    return TextResult(JsonSerializer.Serialize(new
    {
        name,
        count = matches.Length,
        matches,
    }, JsonOpts));
}
```

Plus tool registration in `ToolRegistry.cs` and dispatch in `ToolHandler.Handle`.

#### 2.6 LB-FR-003 — `find_references` includeDeclarations as an operation policy

Per **Correction 2** from the external review:
> "find_references includeDeclarations does not fully fall out of seam #1.
> The resolver can canonicalize IDs and unify partial types, but 'include
> declarations or not' is still a reference-search policy flag on the
> write-side operation. In the current host, FindReferences explicitly checks
> both GetSymbolInfo and GetDeclaredSymbol, so declaration inclusion is an
> operation-level choice, not just an identifier-resolution issue. I'd keep
> FR-003 under seam #1 for shared plumbing, but still model it as an explicit
> FindReferencesOptions.IncludeDeclarations behavior on the host."

So `includeDeclarations` is modeled as an explicit options record on the
write-side host, NOT as a side-effect of resolver merging. The resolver
provides the canonical-ID plumbing underneath; the host decides whether
to walk declaration sites in addition to reference sites.

##### 2.6.1 New options record on the application port

```csharp
// Lifeblood.Application/Ports/Left/ICompilationHost.cs

/// <summary>
/// Options for FindReferences operations. Each flag is a deliberate
/// behavior choice on the live-walker side, NOT something the resolver
/// can decide from a symbol ID alone.
/// </summary>
public sealed class FindReferencesOptions
{
    public static readonly FindReferencesOptions Default = new();

    /// <summary>
    /// When true, the result includes a "(declaration)" entry for every
    /// source location where the symbol is declared. For partial types,
    /// this means one entry per partial declaration file. For non-partial
    /// symbols, exactly one declaration entry. Default false preserves
    /// the existing pure-references-only behavior.
    /// </summary>
    public bool IncludeDeclarations { get; init; }
}
```

##### 2.6.2 Host signature change

```csharp
public interface ICompilationHost
{
    // ... existing members ...

    DomainReferenceLocation[] FindReferences(string symbolId);
    DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options);
}
```

The single-arg overload delegates to the options-aware overload with
`FindReferencesOptions.Default`. Backward compatible — every existing
caller continues to compile and behave identically.

##### 2.6.3 Implementation in `RoslynCompilationHost`

```csharp
public DomainReferenceLocation[] FindReferences(string symbolId)
    => FindReferences(symbolId, FindReferencesOptions.Default);

public DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options)
{
    var refs = /* existing walker logic, using symbolId as before */;

    if (options.IncludeDeclarations)
    {
        // Roslyn's symbol.Locations returns one entry per partial declaration
        // for partial types — exactly the data the user wants surfaced.
        var roslynSymbol = ResolveFromSource(symbolId);
        if (roslynSymbol != null)
        {
            foreach (var location in roslynSymbol.Locations.Where(l => l.IsInSource))
            {
                var span = location.GetMappedLineSpan();
                refs = refs.Append(new DomainReferenceLocation
                {
                    FilePath = span.Path ?? "",
                    Line = span.StartLinePosition.Line + 1,
                    Column = span.StartLinePosition.Character + 1,
                    SpanText = "(declaration)",
                }).ToArray();
            }
        }
    }

    return refs;
}
```

##### 2.6.4 MCP wiring

`Lifeblood.Server.Mcp/WriteToolHandler.HandleFindReferences` reads an
optional `includeDeclarations` boolean from the JSON args:

```csharp
public McpToolResult HandleFindReferences(JsonElement? args)
{
    if (CompilationStateError() is { } error) return error;

    var symbolId = GetString(args, "symbolId");
    if (string.IsNullOrEmpty(symbolId))
        return ErrorResult("symbolId is required");

    // Resolver canonicalizes the input first (Seam #1 plumbing).
    var resolved = _session.Resolver.Resolve(_session.Graph!, symbolId);
    if (resolved.CanonicalId == null)
        return ErrorResult(resolved.Diagnostic ?? $"Symbol not found: {symbolId}");

    // Operation-level policy lives on the options record (Correction 2).
    var includeDecls = GetBool(args, "includeDeclarations") ?? false;
    var options = new FindReferencesOptions { IncludeDeclarations = includeDecls };

    var locations = _session.CompilationHost!.FindReferences(resolved.CanonicalId, options);
    return TextResult(JsonSerializer.Serialize(
        new { symbolId = resolved.CanonicalId, count = locations.Length, locations },
        _jsonOpts));
}
```

The clean split: **resolver canonicalizes the input** (shared plumbing
from Seam #1), **host decides whether to walk declarations** (operation-
level policy from Correction 2). Each layer has one job.

### Test plan for Seam #1

A new file `Lifeblood.Tests/SymbolResolverTests.cs` with **8 tests**:

1. `Resolve_ExactCanonical_FastPath` — canonical input returns itself, outcome `ExactMatch`.
2. `Resolve_TruncatedMethod_SingleOverload` — `method:NS.T.M` (no parens), type T has one method M → returns canonical id, outcome `LenientMethodOverload`.
3. `Resolve_TruncatedMethod_AmbiguousOverloads` — type T has 3 overloads of M → outcome `AmbiguousMethodOverload`, candidates list populated, diagnostic explains.
4. `Resolve_BareShortName_Unique` — `MidiLearnManager` matches one type → returns canonical id, outcome `ShortNameUnique`.
5. `Resolve_BareShortName_Ambiguous` — short name matches multiple types in different namespaces → outcome `AmbiguousShortName`, candidates listed.
6. `Resolve_NotFound_DiagnosticIsHelpful` — input matches nothing → outcome `NotFound`, diagnostic mentions tried strategies.
7. `LookupMerged_PartialType_PrimaryIsFilenameMatch` — three partial files `Foo.cs`, `Foo.Bar.cs`, `Foo.Baz.cs` → primary is `Foo.cs`, FilePaths sorted.
8. `LookupMerged_PartialType_FallsBackToPrefixMatch` — partial files `Foo.A.cs`, `Foo.B.cs` (no bare `Foo.cs`) → primary is `Foo.A.cs` (shortest prefix match).

Plus **1 regression test** in the existing BCL end-to-end integration test
that pins down the LB-BUG-002 misdiagnosis: query `dependants` with the
TRUNCATED form via the resolver and assert it returns the correct callers.

Plus **1 LB-FR-003 test** in `FindReferencesCrossModuleTests.cs`:
`FindReferences_PartialType_IncludeDeclarations_ReturnsAllPartialFiles`.

---

## §3. Seam #2 — Csproj-driven compilation facts (convention, not new abstraction)

> **Correction 3 status — already done.** Compilation creation is already
> extracted to `Internal/ModuleCompilationBuilder.CreateCompilation`. The
> analyzer (`RoslynWorkspaceAnalyzer.AnalyzeWorkspace:114`) only orchestrates
> via `compilationBuilder.ProcessInOrder(...)`. The convention sits on top
> of the existing builder — no extraction step needed before §3.
>
> **Correction 4 status — already done in v2.** Csproj-edit invalidation
> shipped with the BCL fix as `AnalysisSnapshot.CsprojTimestamps` and the
> timestamp loop in `RoslynWorkspaceAnalyzer.IncrementalAnalyze` (BCL plan §8,
> v2 rollout steps 6-8). The new compilation fact `AllowUnsafeCode` ships
> for FREE under that mechanism: when `IncrementalAnalyze` detects a csproj
> timestamp change, it forces re-discovery of that module, which rebuilds
> `ModuleInfo` from scratch — every field on it (BclOwnership, AllowUnsafeCode,
> and any future addition) is refreshed in one shot. **This is the core
> architectural pay-off of doing the BCL fix properly: the next compilation
> fact ships with zero new incremental-invalidation work.**

### The architectural problem

The v2 BCL fix established a pattern: csproj declares facts → discovery parses
them → ModuleInfo stores them → CreateCompilation consumes them. `BclOwnership`
was the first member. But the pattern was never **documented as a contract**,
so the next contributor (or the next finding — LB-BUG-005 / `AllowUnsafeCode`)
hits the same gap because there's no place to look up "how do I add a
csproj-driven compilation option".

The reviewer's BUG-005 is the second instance:

```
Minis.csproj:           <AllowUnsafeBlocks>True</AllowUnsafeBlocks>
RoslynModuleDiscovery:  doesn't read it
ModuleCompilationBuilder: doesn't set CSharpCompilationOptions.AllowUnsafe
Result:                 false-positive CS0227 on every unsafe block in DAWG
```

The fix is structurally identical to BCL ownership. **Same pattern, different
field.** What's missing is the *contract* — write the convention down so the
next addition (LangVersion, Nullable, DefineConstants, Platform) follows the
same shape automatically.

### The architectural answer

**Document the pattern as `INV-COMPFACT-001..003` in `CLAUDE.md`** + ship
`AllowUnsafeCode` as the second instance. NO new types, NO `CompilationFacts`
nested record (premature for two members), just a documented convention plus
the second member.

```
INV-COMPFACT-001 — Csproj is the source of truth for module-level compilation
options. Examples: BclOwnership (v2), AllowUnsafeCode (this fix), LangVersion
(future), Nullable (future), DefineConstants (future), Platform (future).

INV-COMPFACT-002 — Each compilation fact lives as a typed field on
Lifeblood.Application.Ports.Left.ModuleInfo, with a default value that
preserves pre-fix behavior. The field is computed once during
RoslynModuleDiscovery.ParseProject and consumed exactly once during
ModuleCompilationBuilder.CreateCompilation. NEVER re-derive the value at the
compilation layer.

INV-COMPFACT-003 — Csproj edits invalidate cached module facts. The existing
AnalysisSnapshot.CsprojTimestamps tracking in IncrementalAnalyze (added in
the v2 BCL fix, INV-BCL-005) covers every compilation fact for free —
because re-discovery rebuilds the entire ModuleInfo, not just one field.
```

This is **lighter weight** than introducing a `CompilationFacts` nested record
because we have only two members today (BclOwnership + AllowUnsafeCode). When
a future PR adds the fifth member, we can decide whether to extract the nested
record at that point. Avoid premature abstraction.

### Schema additions

#### 3.1 `ModuleInfo.AllowUnsafeCode`

```csharp
public sealed class ModuleInfo
{
    // ... existing fields ...

    /// <summary>
    /// True iff the module's csproj declares <AllowUnsafeBlocks>true</AllowUnsafeBlocks>.
    /// When true, the compilation builder sets CSharpCompilationOptions.AllowUnsafe = true.
    /// Otherwise the compilation rejects `unsafe` blocks with CS0227 and the semantic
    /// model goes silently null inside those blocks (find_references and edge extraction
    /// drop the affected symbols).
    ///
    /// Default false preserves pre-fix behavior. See INV-COMPFACT-001..003.
    /// </summary>
    public bool AllowUnsafeCode { get; init; }
}
```

#### 3.2 `RoslynModuleDiscovery.ParseProject` parses it

```csharp
// AllowUnsafeBlocks is the standard MSBuild property name. Csproj allows
// either case ("true"/"True"); Unity emits "True". Use case-insensitive.
bool allowUnsafe = doc.Descendants()
    .Where(el => el.Name.LocalName == "AllowUnsafeBlocks")
    .Select(el => el.Value)
    .Any(v => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
```

Stored on the returned `ModuleInfo`. Same shape as BCL ownership.

#### 3.3 `ModuleCompilationBuilder.CreateCompilation` consumes it

```csharp
return CSharpCompilation.Create(
    module.Name,
    trees!,
    references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithAllowUnsafe(module.AllowUnsafeCode));
```

One method call. No XML re-parsing.

### Test plan for Seam #2

3 discovery tests in `HardeningTests.cs`:
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` (lowercase) → `AllowUnsafeCode = true`
- `<AllowUnsafeBlocks>True</AllowUnsafeBlocks>` (Unity casing) → `AllowUnsafeCode = true`
- No element → `AllowUnsafeCode = false`

2 compilation tests in `BclOwnershipCompilationTests.cs`:
- `AllowUnsafeCode = true` + source with `unsafe` block → zero CS0227
- `AllowUnsafeCode = false` (default) + source with `unsafe` → CS0227 emitted (proves we only relax when asked)

---

## §4. Seam #3 — `RoslynSemanticView` (typed read-only accessor for adapter state)

### The architectural problem

`RoslynCodeExecutor` holds `_compilations` privately. Scripts can compile pure
C# but cannot reach the loaded semantic state. The reviewer's LB-BUG-003 query
fails because there's no `Workspace`/`Compilations`/`Graph` global on the
script host.

Beyond the script host: **any future tool that needs read access to the
loaded model has the same need.** A debugger, a visualizer, a custom linter,
a benchmarking tool, the documented `INV-LIFEBLOOD-003` use cases (DSP
invariants, architecture metrics, refactoring validation) — all need the same
read-only typed surface. Today, every such consumer would have to thread
`Compilations` + `Graph` + `ModuleDependencies` through its own constructor
as three separate parameters.

### The architectural answer

**The C# adapter publishes `RoslynSemanticView`, a read-only typed accessor
for its loaded semantic state.** Constructed once per `GraphSession.Load()`.
Consumed by `RoslynCodeExecutor` (instead of three raw fields). The script
host's globals object IS a `RoslynSemanticView`. Future consumers reuse it.

```
                   ┌──────────────────────────────────────────┐
                   │ Lifeblood.Server.Mcp.GraphSession        │
                   │  Constructs RoslynSemanticView once per  │
                   │  workspace load (full + incremental).    │
                   └────────────────────┬─────────────────────┘
                                        │
              ┌─────────────────────────┴─────────────────────────┐
              ▼                         ▼                         ▼
      ┌───────────────┐       ┌───────────────────┐       ┌──────────────┐
      │ RoslynCode    │       │ Future debugger,  │       │ Future REPL, │
      │ Executor      │       │ visualizer, etc.  │       │ linter, etc. │
      │  (script host)│       │                   │       │              │
      └───────────────┘       └───────────────────┘       └──────────────┘
              │
              ▼
      Script globals = RoslynSemanticView
      Scripts reach Roslyn via:
        Graph.SymbolsOfKind(SymbolKind.Method).Count()
        Compilations["Foo"].GlobalNamespace
        ModuleDependencies["Foo"]
```

### Schema additions

#### 4.1 `Lifeblood.Adapters.CSharp/RoslynSemanticView.cs` (NEW)

```csharp
namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Read-only typed accessor for the C# adapter's loaded semantic state.
/// Constructed once per RoslynWorkspaceAnalyzer.AnalyzeWorkspace() call.
/// Consumed by RoslynCodeExecutor and any other tool that needs read
/// access to the loaded compilations + graph.
///
/// This is the script-globals object passed to lifeblood_execute scripts.
/// Scripts access it via top-level identifiers Graph, Compilations,
/// ModuleDependencies (CSharpScript.RunAsync<TGlobals> exposes instance
/// members at script scope).
///
/// See INV-VIEW-001..003 in CLAUDE.md.
/// </summary>
public sealed class RoslynSemanticView
{
    public IReadOnlyDictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation> Compilations { get; }
    public Lifeblood.Domain.Graph.SemanticGraph Graph { get; }
    public IReadOnlyDictionary<string, string[]> ModuleDependencies { get; }

    public RoslynSemanticView(
        IReadOnlyDictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations,
        Lifeblood.Domain.Graph.SemanticGraph graph,
        IReadOnlyDictionary<string, string[]> moduleDependencies)
    {
        Compilations = compilations;
        Graph = graph;
        ModuleDependencies = moduleDependencies;
    }
}
```

This lives in the C# adapter. It's Roslyn-typed on purpose — exposing
`CSharpCompilation` is the whole point. Other language adapters publish
their own view type using their language's semantic model.

#### 4.2 `RoslynCodeExecutor` takes a `RoslynSemanticView`

Constructor signature changes from
`RoslynCodeExecutor(IReadOnlyDictionary<string, CSharpCompilation>)`
to `RoslynCodeExecutor(RoslynSemanticView)`. The class stores `_view` instead
of `_compilations` and uses `_view.Compilations` / `_view.Graph` /
`_view.ModuleDependencies` internally.

In `Execute()`, the view IS the script globals — pass it directly:

```csharp
var scriptTask = Task.Run(() =>
    CSharpScript.RunAsync(code, options,
        globals: _view,
        globalsType: typeof(RoslynSemanticView),
        cancellationToken: cts.Token).GetAwaiter().GetResult(),
    cts.Token);
```

Add `Lifeblood.Domain.Graph`, `Lifeblood.Adapters.CSharp`,
`Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp` to the script's
default imports so scripts can `using` Roslyn types directly.

#### 4.3 `GraphSession.Load` constructs the view once

`Lifeblood.Server.Mcp/GraphSession.cs` constructs `RoslynCodeExecutor` in
`Load()` (line 98) and `LoadIncremental()` (line 148). Build the view once,
pass it to the executor:

```csharp
if (adapter.Compilations is { Count: > 0 })
{
    var view = new RoslynSemanticView(
        adapter.Compilations,
        graph,
        adapter.ModuleDependencies ?? new Dictionary<string, string[]>(StringComparer.Ordinal));

    newCompilationHost = new RoslynCompilationHost(adapter.Compilations, adapter.ModuleDependencies);
    newCodeExecutor = new RoslynCodeExecutor(view);
    newRefactoring = new RoslynWorkspaceRefactoring(adapter.Compilations, adapter.ModuleDependencies);
}
```

Future consumers (a debugger, etc.) take the same `view` reference.

### Test plan for Seam #3

3 new tests in `Lifeblood.Tests/RoslynSemanticViewTests.cs`:

1. `Execute_GraphGlobal_ReturnsSymbolCount` — script does `return Graph.Symbols.Count;` against a single-module workspace, asserts > 0.
2. `Execute_CompilationsGlobal_ListsModules` — script does `return Compilations.Keys.Count();` and asserts the module is present.
3. `Execute_PureScriptWithoutGlobals_StillWorks` — backward compat: `return Enumerable.Range(0, 5).Sum();` continues to work unchanged. The view is additive — scripts that don't reference it are unaffected.

Plus 1 sanity test that pins down `RoslynSemanticView` constructor takes
the three fields and exposes them as read-only properties. (~5 lines.)

---

## §5. Combined rollout (post-approval)

All three seams ship as **one PR**. Sequential commits, full suite green
between each. **18 steps total** (vs 26 in the piecemeal v3 plan).

### Seam #1 — `ISymbolResolver` (8 steps)

1. **Domain — short-name index** in `SemanticGraph.GraphIndexes`. Public
   `FindByShortName(name)` accessor. Suite: 310.
2. **Application port — `ISymbolResolver` interface** + the single
   `SymbolResolutionResult` DTO + `ShortNameMatch` + `ResolveOutcome` enum.
   No `MergedSymbol` (collapsed into `SymbolResolutionResult` per
   Correction 1). Suite: 310.
3. **Connector — `LifebloodSymbolResolver` implementation** with the
   four-stage resolver and partial-type merge via Contains edges (built
   inside `Resolve`, no separate `LookupMerged` method). Includes
   `ChoosePrimaryFilePath` rules from §2.3. Suite: 310.
4. **Resolver tests** — 8 unit tests in `SymbolResolverTests.cs`. Suite: 318.
5. **MCP routing — Lookup, Dependants, Dependencies, BlastRadius, FileImpact**
   all route through the resolver. Each handler reads
   `resolved.PrimaryFilePath` / `resolved.DeclarationFilePaths` from the
   single result DTO. Suite: 318.
6. **MCP routing tests** — extend existing handler tests to assert truncated
   inputs resolve correctly. ~3 tests, including the LB-BUG-002 regression
   pinning down `dependants` with the truncated `method:Voice.SetPatch` form.
   Suite: 321.
7. **New tool — `lifeblood_resolve_short_name`** + tool registration +
   dispatch + JSON schema. Suite: 321.
8. **LB-FR-003 — `FindReferencesOptions.IncludeDeclarations`** on the
   write-side host + MCP handler wiring + 1 test. (Per Correction 2:
   modeled as an explicit operation policy on the host, not as a
   side-effect of resolver merging.) Suite: 322.

### Seam #2 — `AllowUnsafeCode` compilation fact (5 steps)

9. **Application schema — `ModuleInfo.AllowUnsafeCode`** field. Suite: 322.
10. **Discovery — parse `<AllowUnsafeBlocks>` in `ParseProject`**. Suite: 322.
11. **Discovery tests** — 3 cases (lowercase, Unity casing, absent). Suite: 325.
12. **Compilation — `WithAllowUnsafe(module.AllowUnsafeCode)` in `CreateCompilation`**. Suite: 325.
13. **Compilation tests** — 2 cases (allows unsafe, default rejects unsafe). Suite: 327.

### Seam #3 — `RoslynSemanticView` (4 steps)

14. **Adapter — `RoslynSemanticView` POCO**. Suite: 327.
15. **Adapter — `RoslynCodeExecutor` constructor + `Execute` rewire** to use
    the view as both internal state and script globals. Suite: 327.
16. **Server — `GraphSession.Load` and `LoadIncremental`** wire the new
    constructor in both code paths. Suite: 327.
17. **`RoslynSemanticView` tests** — 3 script-globals tests + 1 view-shape
    sanity test. Suite: 331.

### Acceptance (1 step)

18. **DAWG end-to-end verification** (after publish):
    - `lifeblood_lookup type:Nebulae.BeatGrid.AdaptiveBeatGrid` → returns
      `AdaptiveBeatGrid.cs` with `FilePaths[]` listing all 150+ partials.
    - `lifeblood_diagnose moduleName="Minis"` → CS0227 diagnostics gone.
    - `lifeblood_execute "return Graph.Symbols.Count;"` → returns ~44438.
    - `lifeblood_execute "return Compilations.Count;"` → returns 75.
    - `lifeblood_dependants method:Nebulae.BeatGrid.Audio.DSP.Voice.SetPatch`
      (truncated) → returns 8 callers via the resolver.
    - `lifeblood_resolve_short_name MidiLearnManager` → returns one match
      with the canonical type ID.
    - `lifeblood_find_references type:Nebulae.BeatGrid.AdaptiveBeatGrid includeDeclarations=true`
      → returns reference sites + all partial declaration files.

After acceptance: CHANGELOG entry, CLAUDE.md invariant section, publish to dist.

---

## §6. CLAUDE.md additions (the lasting architectural contracts)

### `INV-RESOLVER-001` through `INV-RESOLVER-004`

```
INV-RESOLVER-001 — Identifier resolution is a port. Lifeblood.Application.Ports.Right.ISymbolResolver
is the single entry point for converting user-supplied symbol identifiers
into canonical IDs. Every read-side MCP tool that takes a `symbolId`
parameter (lookup, dependants, dependencies, blast_radius, file_impact, etc.)
MUST route through ISymbolResolver before doing any graph lookup. NEVER
add a read-side tool that calls graph.GetSymbol or graph.GetIncomingEdgeIndexes
directly with the user's raw input.

INV-RESOLVER-002 — The resolver accepts every input format and returns a
canonical ID OR a candidate list with a diagnostic. Supported inputs:
exact canonical ID, truncated method form (no parens), bare short name.
Resolution order: exact match → lenient method overload → short-name index.

INV-RESOLVER-003 — Partial-type unification happens at the resolver layer,
not at graph build time. The graph stores raw symbols (one per partial
declaration; last-write-wins is fine). The resolver's Resolve() method
walks the type's incoming Contains edges to discover all partial
declaration files and populates SymbolResolutionResult.PrimaryFilePath
(deterministic) + SymbolResolutionResult.DeclarationFilePaths (full set).
Schema unchanged — Lifeblood.Domain.Graph.Symbol.FilePath stays a single
value. The merged view lives entirely on the resolution result DTO,
preserving domain purity.

INV-RESOLVER-004 — Primary file path for partial types is chosen
deterministically: filename matches type name → filename starts with
"<TypeName>." (shortest first) → lexicographic first. Same input + same
graph → same primary, always.
```

### `INV-COMPFACT-001` through `INV-COMPFACT-003`

```
INV-COMPFACT-001 — Csproj is the source of truth for module-level compilation
options. Examples: BclOwnership (v2), AllowUnsafeCode (this fix). Future
additions: LangVersion, Nullable, DefineConstants, Platform, WarningLevel.

INV-COMPFACT-002 — Each compilation fact lives as a typed field on
ModuleInfo, with a default value that preserves pre-fix behavior. Computed
once during RoslynModuleDiscovery.ParseProject. Consumed exactly once during
ModuleCompilationBuilder.CreateCompilation. NEVER re-derive at the
compilation layer; NEVER sniff filenames as a substitute for declared options.

INV-COMPFACT-003 — Csproj edits invalidate cached module facts. The
AnalysisSnapshot.CsprojTimestamps tracking added in v2 (INV-BCL-005)
covers every compilation fact for free — re-discovery rebuilds the entire
ModuleInfo, not just one field.
```

### `INV-VIEW-001` through `INV-VIEW-003`

```
INV-VIEW-001 — Each language adapter publishes a typed read-only accessor
for its loaded semantic state. The C# adapter publishes RoslynSemanticView
(Compilations, Graph, ModuleDependencies). Future language adapters publish
their own view type using their language's semantic model.

INV-VIEW-002 — Tools that need read access to the loaded model consume the
adapter's view, not raw fields. The script host (RoslynCodeExecutor) takes
a RoslynSemanticView in its constructor. Future consumers (debuggers,
visualizers, linters, REPLs) take the same view. NEVER thread raw
Compilations / Graph / ModuleDependencies through individual tool
constructors.

INV-VIEW-003 — The view is constructed once per workspace load
(full or incremental) by GraphSession and shared across consumers by
reference. Construction is cheap (POCO with three field assignments);
sharing avoids accidental divergence between consumers' views of the
same workspace.
```

These nine new invariants are the lasting architectural contracts. Each
prevents the next contributor from reintroducing the same gap.

---

## §7. Backward compatibility

- **Domain schema unchanged.** No new field on `Symbol`. The merged view
  is computed by the resolver from existing Contains edges.
- **`SemanticGraph` gains a new internal index** (short-name) plus one new
  public method (`FindByShortName`). Additive.
- **`Symbol.FilePath` remains a single string.** The resolver's `MergedSymbol`
  exposes `FilePaths[]` as a separate read-side concept.
- **`ModuleInfo` gains one field** (`AllowUnsafeCode`, default false). Same
  shape as the v2 BCL fix.
- **`RoslynCodeExecutor` constructor signature changes** — but it's
  instantiated in exactly two places (`GraphSession.Load`,
  `GraphSession.LoadIncremental`), both updated in the same commit.
- **Existing pure-C# scripts** continue to work unchanged because the script
  globals are passive: members exposed at top-level scope, scripts that don't
  reference them are unaffected.
- **All 310 existing tests** should pass after every commit. The combined
  delta is +21 new tests (8 resolver + 3 routing + 1 includeDeclarations
  + 3 unsafe-discovery + 2 unsafe-compilation + 3 script-globals + 1 view
  sanity), bringing the suite to 331.

---

## §8. Out of scope (deliberately)

- **`Workspace` (AdhocWorkspace) global on the script host** — the reviewer's
  literal test was `Workspace.CurrentSolution.Projects.Count()`. v4 exposes
  `Compilations` + `Graph` + `ModuleDependencies` directly because that's
  what the documented use cases need. The Roslyn-typed surface
  (`RoslynSemanticView`) is open for future extension; if a user explicitly
  needs the AdhocWorkspace, expose it as a fourth property on the view.
- **Multi-target csprojs** — `<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>`.
  Out of scope. Same documented limit as v2.
- **NuGet `project.assets.json` incremental invalidation** — out of scope; full
  re-analyze fixes it.
- **`Directory.Build.props` change detection** — out of scope; full re-analyze.
- **Other compilation facts beyond `AllowUnsafeCode`** — `LangVersion`,
  `Nullable`, `DefineConstants`, `Platform`. Each follows INV-COMPFACT-001..003
  via the same pattern. We're shipping only the one flagged today.
- **`CompilationFacts` nested record on `ModuleInfo`** — premature for two
  members. When a future PR adds the fifth member, decide whether to extract
  the nested record then.
- **The remaining backlog items** (Finding D `list_members`,
  find_writes/find_reads separation, cache warm/cold indicator).
  Tracked separately.

---

## §9. Risk assessment

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Resolver routing breaks an existing handler | LOW | MEDIUM | Exact-match fast path returns canonical input unchanged for all currently-passing tests. Explicit regression tests for the truncated forms. |
| `LookupMerged` partial-type discovery via Contains edges misses some files | LOW | MEDIUM | Audit during step 3: walk the existing Contains edges in DAWG for `AdaptiveBeatGrid` and assert all 150+ partials are discovered. Backstop: if Contains edges are missing for some files, fall back to the symbol's `Locations` from Roslyn (which is always populated). |
| `ChoosePrimaryFilePath` picks wrong file for unusual partial conventions | LOW | LOW | Three-rule fallback (filename match → prefix match → lexicographic) covers the common cases. Edge cases refine via test additions. |
| `Symbol.FilePath` consumers expecting non-determinism break | NONE | LOW | The previous behavior was already non-deterministic (last-write-wins by file iteration order). Making it deterministic is strictly better. |
| New `WithAllowUnsafe(true)` call breaks unrelated tests | LOW | LOW | Default `false` preserves prior behavior. |
| `RoslynCodeExecutor` constructor change breaks downstream consumers | LOW | LOW | Two call sites, both in `GraphSession`, both in the same commit. |
| `RoslynSemanticView` exposes mutable state | LOW | HIGH | `CSharpCompilation` is immutable in Roslyn; script can READ but mutation requires `WithX()` which produces a new compilation that doesn't affect Lifeblood's state. AST blocklist still applies. |
| Three-seam framing introduces hidden coupling between commits | LOW | LOW | Each seam is independent: Seam #1 doesn't need #2 or #3, etc. The ordering (1 → 2 → 3) is by leverage, not dependency. |
| `INV-RESOLVER-003` (graph stays raw, merge on read) hurts performance | LOW | LOW | The merge walk is O(in-degree) per partial type lookup. For AdaptiveBeatGrid with 150 partials, that's ~150 dict lookups per `lookup` call — well under 1ms. Cache the merged view if profiling ever reveals a hot path. |

---

## §10. Approval

**Awaiting Matic's "go" on this v4 plan after a fresh Claude review pass.**

The v4 plan addresses every reviewer finding via three architectural seams:

| Finding | Closed by |
|---|---|
| LB-BUG-002 (truncated symbol ID) | Seam #1 — `ISymbolResolver` |
| LB-BUG-003 (execute Workspace global) | Seam #3 — `RoslynSemanticView` |
| LB-BUG-004 (partial type lookup) | Seam #1 — `LookupMerged` partial unification |
| LB-BUG-005 (allowUnsafeCode) | Seam #2 — `INV-COMPFACT-001..003` + `AllowUnsafeCode` instance |
| LB-FR-002 (short-name resolver) | Seam #1 — same resolver |
| LB-FR-003 (find_references include declarations) | Seam #1 — small additive on top of LookupMerged |

**Three seams = three architectural contracts that prevent the same bug
classes from recurring.** The 9 new invariants in CLAUDE.md are the
durable artifact: the next contributor finds the contract documented and
adds the next compilation option / next read-side tool / next adapter
consumer through the established pattern, not by re-deriving it ad-hoc.

If approved, the rollout is sequential through the 18 steps in §5. Each
commit covers one logical layer. The full suite must pass after every commit.
The DAWG verification in step 18 is the final acceptance gate.

Total work: ~600 lines of code, ~21 new tests, 4 new files (`ISymbolResolver`,
`LifebloodSymbolResolver`, `RoslynSemanticView`, `SymbolResolverTests`),
~6 modified files. Three seams. Five findings closed.
