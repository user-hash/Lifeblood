using Lifeblood.Application.Ports.Right;
using Lifeblood.Domain.Graph;

namespace Lifeblood.Connectors.Mcp;

/// <summary>
/// Reference implementation of <see cref="ISymbolResolver"/>. Single source
/// of truth for "what does this user-supplied symbol identifier mean" across
/// every read-side MCP tool. See INV-RESOLVER-001..004 in CLAUDE.md.
///
/// Resolution order inside <see cref="Resolve"/>:
/// <list type="number">
///   <item><b>Step 0 — input canonicalization.</b> The user-supplied
///     identifier is rewritten via the injected
///     <see cref="IUserInputCanonicalizer"/> BEFORE any lookup runs. For
///     the C# adapter this rewrites <c>System.String</c> → <c>string</c>,
///     strips <c>global::</c> prefixes, etc. Step 0 is unconditional and
///     runs exactly once per Resolve call. From here on, every diagnostic,
///     every <c>Candidates[]</c> entry, and every log line references the
///     CANONICAL form, not the user's raw input. This is the rule that
///     replaces the earlier "alias retry as final fallback" pattern —
///     canonicalization is NOT a retry, it's the first step of the
///     pipeline, and the grammar mismatch is fixed at the boundary
///     instead of at the lookup layer. See Ground Rule 1 of the plan in
///     <c>.claude/plans/improvement-master-2026-04-11.md</c>.</item>
///   <item>Exact canonical match (fast path; <see cref="ResolveOutcome.ExactMatch"/>).</item>
///   <item>Truncated method form <c>method:NS.Type.Name</c> with no parens —
///     lenient single-overload match
///     (<see cref="ResolveOutcome.LenientMethodOverload"/>) or ambiguous
///     (<see cref="ResolveOutcome.AmbiguousMethodOverload"/>).</item>
///   <item>Bare short name with no kind prefix and no namespace dots —
///     unique (<see cref="ResolveOutcome.ShortNameUnique"/>) or ambiguous
///     (<see cref="ResolveOutcome.AmbiguousShortName"/>).</item>
///   <item>Otherwise <see cref="ResolveOutcome.NotFound"/>.</item>
/// </list>
///
/// Partial-type unification is computed inline by walking the existing
/// <see cref="EdgeKind.Contains"/> edges from <see cref="SymbolKind.File"/>
/// symbols. The graph is unchanged — partial-type read model is on the
/// resolution result, not on <see cref="Symbol"/>.
/// </summary>
public sealed class LifebloodSymbolResolver : ISymbolResolver
{
    private readonly IUserInputCanonicalizer _canonicalizer;

    /// <summary>
    /// Constructor with an explicit canonicalizer. Composition roots pass
    /// <see cref="Lifeblood.Adapters.CSharp.CSharpUserInputCanonicalizer"/>
    /// for C# workspaces. Tests and language-agnostic scenarios pass
    /// <see cref="IdentityUserInputCanonicalizer.Instance"/>.
    /// </summary>
    public LifebloodSymbolResolver(IUserInputCanonicalizer canonicalizer)
    {
        _canonicalizer = canonicalizer ?? throw new System.ArgumentNullException(nameof(canonicalizer));
    }

    /// <summary>
    /// Parameterless default for backward-compatible call sites. Uses the
    /// identity canonicalizer, which means primitive-alias rewriting is OFF.
    /// Prefer the explicit constructor so the composition root's adapter
    /// choice flows through.
    /// </summary>
    public LifebloodSymbolResolver() : this(IdentityUserInputCanonicalizer.Instance) { }

    public SymbolResolutionResult Resolve(SemanticGraph graph, string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return new SymbolResolutionResult
            {
                Outcome = ResolveOutcome.NotFound,
                Diagnostic = "Empty symbol identifier.",
            };
        }

        // Step 0: canonicalize the user-supplied identifier. Every subsequent
        // step operates on the canonical form, and every diagnostic quotes
        // the canonical form. See the XML doc above and Ground Rule 1.
        var canonical = _canonicalizer.Canonicalize(userInput);

        // Rule 1: exact canonical match. Fast path for callers that already
        // have a fully-qualified ID (e.g., copied from a previous tool result).
        var exact = graph.GetSymbol(canonical);
        if (exact != null)
            return AttachOverloads(graph, BuildResolved(graph, canonical, exact, ResolveOutcome.ExactMatch));

        // Rule 2: truncated method form. Pattern: "method:NS.Type.Name" with
        // no parameter parens. We try to find every method on NS.Type whose
        // simple name matches and either return the unique overload or
        // surface ambiguity.
        if (TryParseMethodWithoutParens(canonical, out var typeId, out var methodName))
        {
            var overloads = FindOverloadsOnType(graph, typeId, methodName);
            if (overloads.Length == 1)
                return AttachOverloads(graph, BuildResolved(graph, overloads[0].Id, overloads[0],
                    ResolveOutcome.LenientMethodOverload));
            if (overloads.Length > 1)
            {
                return new SymbolResolutionResult
                {
                    Outcome = ResolveOutcome.AmbiguousMethodOverload,
                    Candidates = overloads.Select(o => o.Id).ToArray(),
                    Overloads = overloads.Select(o => BuildOverloadInfo(o)).ToArray(),
                    Diagnostic = $"Method '{methodName}' on '{typeId}' has " +
                                 $"{overloads.Length} overloads. Pick one from Candidates.",
                };
            }
            // Fall through to short-name and not-found rules.
        }

        // Rule 3: bare short name. No kind prefix and no namespace dots —
        // user is shorthand-querying, e.g. "MidiLearnManager".
        if (LooksLikeBareShortName(canonical))
        {
            var matches = graph.FindByShortName(canonical);
            if (matches.Count == 1)
                return AttachOverloads(graph, BuildResolved(graph, matches[0].Id, matches[0],
                    ResolveOutcome.ShortNameUnique));
            if (matches.Count > 1)
            {
                return new SymbolResolutionResult
                {
                    Outcome = ResolveOutcome.AmbiguousShortName,
                    Candidates = matches.Select(s => s.Id).ToArray(),
                    Diagnostic = $"Short name '{canonical}' matches {matches.Count} " +
                                 "symbols across different namespaces. Pick one from Candidates " +
                                 "or query lifeblood_resolve_short_name for details.",
                };
            }
        }

        // Rule 4: extracted-short-name fallback for prefixed / qualified inputs
        // whose namespace is wrong or stale. The user typed something like
        //   type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter
        // when the real symbol lives in
        //   type:Nebulae.BeatGrid.Audio.Tuning.VoicePatchAdapter
        // Rules 1-3 all passed because of the kind prefix / namespace dots,
        // so the bare short-name path never fired. Extract the trailing
        // short-name segment here and look it up in the same short-name
        // index rule 3 would have used. If it hits exactly one symbol we
        // silently resolve to that symbol (ResolveOutcome.ShortNameFromQualifiedInput);
        // if multiple we surface every candidate (AmbiguousShortNameFromQualifiedInput);
        // if zero we fall through to the not-found diagnostic.
        //
        // This is the fix for the dogfood report where "Did you mean" suggestions
        // were three unrelated MixerScreenAdapter properties despite the user's
        // short name being uniquely resolvable via graph.FindByShortName — the
        // old suggestion ranker was scoring the full canonical-shaped input
        // string against bare symbol names, which Levenshtein'd toward length
        // coincidence rather than semantic match. See INV-RESOLVER-005.
        var extractedShortName = ExtractLikelyShortName(canonical);
        bool triedExtractedShortName = false;
        if (!string.IsNullOrEmpty(extractedShortName)
            && !string.Equals(extractedShortName, canonical, System.StringComparison.Ordinal))
        {
            triedExtractedShortName = true;
            var extractedMatches = graph.FindByShortName(extractedShortName);
            if (extractedMatches.Count == 1)
            {
                var result = AttachOverloads(graph, BuildResolved(
                    graph, extractedMatches[0].Id, extractedMatches[0],
                    ResolveOutcome.ShortNameFromQualifiedInput));
                // Preserve the explanatory diagnostic so tools can surface
                // "we interpreted X as Y" instead of silently re-pointing.
                return new SymbolResolutionResult
                {
                    CanonicalId = result.CanonicalId,
                    Outcome = result.Outcome,
                    Symbol = result.Symbol,
                    PrimaryFilePath = result.PrimaryFilePath,
                    DeclarationFilePaths = result.DeclarationFilePaths,
                    Candidates = result.Candidates,
                    Overloads = result.Overloads,
                    Diagnostic = $"Resolved '{canonical}' via short-name fallback: " +
                                 $"the namespace did not match any symbol, but the trailing " +
                                 $"segment '{extractedShortName}' uniquely identifies " +
                                 $"{extractedMatches[0].Id}.",
                };
            }
            if (extractedMatches.Count > 1)
            {
                return new SymbolResolutionResult
                {
                    Outcome = ResolveOutcome.AmbiguousShortNameFromQualifiedInput,
                    Candidates = extractedMatches.Select(s => s.Id).ToArray(),
                    Diagnostic = $"Symbol not found: {canonical}. " +
                                 $"The trailing short name '{extractedShortName}' matches " +
                                 $"{extractedMatches.Count} symbols across different namespaces. " +
                                 "Pick one from Candidates.",
                };
            }
        }

        // Not-found diagnostic surfaces ranked near-matches so every dead-end
        // response carries next-steps. The scorer is the same one used by
        // ResolutionMode.Fuzzy and by SuggestNearMatches, so the order is
        // deterministic and consistent with the explicit fuzzy tool path.
        // The ranker itself now routes through ExtractLikelyShortName so
        // suggestions for prefixed inputs rank against the trailing segment
        // instead of the entire canonical-shaped string.
        var suggestions = SuggestNearMatchesInternal(graph, canonical, limit: 5);
        var suggestionText = suggestions.Length == 0
            ? ""
            : " Did you mean: " + string.Join(", ", suggestions.Take(3).Select(s => s.CanonicalId)) + "?";

        var triedDescription = triedExtractedShortName
            ? "Tried exact canonical match, lenient method overload, bare short-name lookup, " +
              $"and extracted short-name fallback ('{extractedShortName}')."
            : "Tried exact canonical match, lenient method overload, and short-name lookup.";

        return new SymbolResolutionResult
        {
            Outcome = ResolveOutcome.NotFound,
            Candidates = suggestions.Select(s => s.CanonicalId).ToArray(),
            Diagnostic = $"Symbol not found: {canonical}. " + triedDescription + suggestionText,
        };
    }

    public ShortNameMatch[] ResolveShortName(SemanticGraph graph, string shortName, ResolutionMode mode = ResolutionMode.Exact)
    {
        if (string.IsNullOrWhiteSpace(shortName))
            return System.Array.Empty<ShortNameMatch>();

        // Short names have no BCL aliases to rewrite, but we still run
        // canonicalization for consistency so a caller who passes
        // `System.String` is silently redirected to `string`.
        var canonical = _canonicalizer.Canonicalize(shortName);

        ShortNameMatch[] matches = mode switch
        {
            ResolutionMode.Exact => graph.FindByShortName(canonical)
                .Select(s => ToShortNameMatch(s))
                .ToArray(),
            ResolutionMode.Contains => graph.Symbols
                .Where(s => !string.IsNullOrEmpty(s.Name) &&
                            s.Name.Contains(canonical, StringComparison.OrdinalIgnoreCase))
                .Select(s => ToShortNameMatch(s))
                .ToArray(),
            ResolutionMode.Fuzzy => SuggestNearMatchesInternal(graph, canonical, limit: 20),
            _ => System.Array.Empty<ShortNameMatch>(),
        };

        // Zero-result post-hook: every mode surfaces ranked suggestions when
        // its own search yields nothing. That's what makes empty responses
        // useful instead of dead ends. The suggestions share scoring with
        // Fuzzy so the ordering is stable.
        if (matches.Length == 0)
            return SuggestNearMatchesInternal(graph, canonical, limit: 5);

        return matches;
    }

    public ShortNameMatch[] SuggestNearMatches(SemanticGraph graph, string shortName, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(shortName))
            return System.Array.Empty<ShortNameMatch>();
        var canonical = _canonicalizer.Canonicalize(shortName);
        return SuggestNearMatchesInternal(graph, canonical, limit);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Fuzzy scoring
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rank symbols against the query and return the top
    /// <paramref name="limit"/>. Deterministic for a given input.
    ///
    /// Two-stage ranking:
    /// <list type="number">
    ///   <item><b>Short-name index hits (primary).</b> The query is passed
    ///     through <see cref="ExtractLikelyShortName"/> first. If the
    ///     extracted segment produces any exact hits via
    ///     <see cref="SemanticGraph.FindByShortName"/>, those land at
    ///     score <see cref="ShortNameHitScore"/> — far above any fuzzy
    ///     score so they always sort to the top.</item>
    ///   <item><b>Fuzzy ranker (secondary).</b> Every named symbol is
    ///     scored via <see cref="ScoreCandidate"/> against the extracted
    ///     short name (or the raw query if extraction was a no-op). Scoring:
    ///     <list type="bullet">
    ///       <item>+10 if the query is a case-insensitive substring of the candidate's simple name.</item>
    ///       <item>+5 if the query is a case-insensitive prefix of any CamelCase-split token of the candidate.</item>
    ///       <item>+3 × max(0, candidateLength − levenshteinDistance).</item>
    ///     </list>
    ///   </item>
    /// </list>
    /// Ties are broken lexicographically on the canonical ID for stable
    /// ordering.
    ///
    /// The ExtractLikelyShortName step is load-bearing. Before it, callers
    /// that passed a full canonical-shaped input (e.g.
    /// <c>type:Foo.Bar.VoicePatchAdapter</c>) got ranked by Levenshtein
    /// distance over the ENTIRE string, which biased the ranking toward
    /// accidentally-long candidate names because
    /// <c>closeness = candidateLength - distance</c> grows with candidate
    /// length. Two independent dogfood reports landed on three unrelated
    /// <c>MixerScreenAdapter.…ActivePresetName</c> suggestions as the
    /// "best" matches. See INV-RESOLVER-005.
    /// </summary>
    internal static ShortNameMatch[] SuggestNearMatchesInternal(SemanticGraph graph, string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return System.Array.Empty<ShortNameMatch>();

        // Extract the trailing short-name segment. Prefixed/qualified
        // inputs rank against the segment; bare short names rank against
        // themselves (extraction is a no-op).
        var extracted = ExtractLikelyShortName(query);
        var fuzzyQuery = string.IsNullOrEmpty(extracted) ? query : extracted;

        // Merge both ranking sources in one dictionary keyed by canonical
        // id. Short-name index hits get ShortNameHitScore; fuzzy hits get
        // the ScoreCandidate result. If a symbol lands in both, the higher
        // score wins — short-name hits always dominate because
        // ShortNameHitScore is deliberately above the max reachable
        // ScoreCandidate value for any realistic candidate.
        var scored = new Dictionary<string, (int score, Symbol sym)>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(extracted) && !string.Equals(extracted, query, System.StringComparison.Ordinal))
        {
            foreach (var sym in graph.FindByShortName(extracted))
            {
                scored[sym.Id] = (ShortNameHitScore, sym);
            }
        }

        foreach (var sym in graph.Symbols)
        {
            if (string.IsNullOrEmpty(sym.Name)) continue;
            var score = ScoreCandidate(fuzzyQuery, sym.Name);
            if (score <= 0) continue;
            if (scored.TryGetValue(sym.Id, out var existing))
            {
                if (score > existing.score) scored[sym.Id] = (score, sym);
            }
            else
            {
                scored[sym.Id] = (score, sym);
            }
        }

        return scored.Values
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.sym.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(t => ToShortNameMatch(t.sym))
            .ToArray();
    }

    /// <summary>
    /// Score awarded to a literal short-name-index hit. Deliberately far
    /// above the maximum reachable <see cref="ScoreCandidate"/> value so
    /// short-name hits always rank above fuzzy matches. Pinned by
    /// <c>ResolverSuggestionRankingTests</c>.
    /// </summary>
    internal const int ShortNameHitScore = 1000;

    /// <summary>
    /// Strip the kind prefix (everything up to and including the first
    /// <c>:</c>), strip any method parameter list <c>(...)</c>, and return
    /// the final dot-separated segment. Returns the empty string for
    /// null/whitespace input.
    ///
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>type:Nebulae.BeatGrid.Audio.DSP.VoicePatchAdapter</c> → <c>VoicePatchAdapter</c></item>
    ///   <item><c>method:App.Svc.Do(int)</c> → <c>Do</c></item>
    ///   <item><c>App.Svc.Do</c> → <c>Do</c></item>
    ///   <item><c>MidiLearnManager</c> → <c>MidiLearnManager</c> (no-op)</item>
    ///   <item><c>type:Foo</c> → <c>Foo</c></item>
    /// </list>
    /// Pure function. No graph access, no side effects. Pinned by unit tests.
    /// </summary>
    internal static string ExtractLikelyShortName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // Strip kind prefix (first colon and everything before it).
        var colonIdx = input.IndexOf(':');
        var working = colonIdx >= 0 ? input.Substring(colonIdx + 1) : input;

        // Strip method parameter list if present.
        var parenIdx = working.IndexOf('(');
        if (parenIdx >= 0) working = working.Substring(0, parenIdx);

        // Take the last dot-separated segment.
        var lastDot = working.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < working.Length - 1)
            working = working.Substring(lastDot + 1);

        return working.Trim();
    }

    /// <summary>
    /// Score one candidate against the query. Zero means "drop this
    /// candidate from the ranking entirely" (the scoring is additive and
    /// every non-zero contribution means the candidate is at least weakly
    /// related to the query).
    /// </summary>
    internal static int ScoreCandidate(string query, string candidateName)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidateName)) return 0;
        int score = 0;
        if (candidateName.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10;
        foreach (var token in SplitCamelCase(candidateName))
        {
            if (token.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
                break;
            }
        }
        var distance = LevenshteinDistance(
            query.ToLowerInvariant(), candidateName.ToLowerInvariant());
        var closeness = candidateName.Length - distance;
        if (closeness > 0) score += 3 * closeness;
        return score;
    }

    private static IEnumerable<string> SplitCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) yield break;
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                yield return name.Substring(start, i - start);
                start = i;
            }
        }
        yield return name.Substring(start);
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = System.Math.Min(
                    System.Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static ShortNameMatch ToShortNameMatch(Symbol sym) => new()
    {
        CanonicalId = sym.Id,
        FilePath = sym.FilePath,
        Kind = sym.Kind.ToString(),
    };

    // ────────────────────────────────────────────────────────────────────────
    // Overload surfacing (LB-INBOX-004)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When a resolution landed on a method symbol, populate the result's
    /// <see cref="SymbolResolutionResult.Overloads"/> with every sibling
    /// overload on the same containing type. For non-method resolutions,
    /// returns the result unchanged.
    ///
    /// The modeling rationale (Phase 3 step 4 of the plan): LB-INBOX-004
    /// asks for per-overload canonical IDs whenever resolution lands on a
    /// method, so callers who copy-paste a method name get back a full
    /// picker of overloads instead of just the one the resolver happened
    /// to match. This shape lives on <c>Resolve()</c>, not on
    /// <c>ResolveShortName()</c>, because <c>ResolveShortName</c> already
    /// returns a flat list of matches (one entry per canonical ID; method
    /// overloads already appear as separate entries). Attaching overloads
    /// twice would double-count.
    /// </summary>
    private static SymbolResolutionResult AttachOverloads(SemanticGraph graph, SymbolResolutionResult result)
    {
        if (result.Symbol == null) return result;
        if (result.Symbol.Kind != SymbolKind.Method) return result;
        if (string.IsNullOrEmpty(result.Symbol.ParentId)) return result;

        var siblings = FindOverloadsOnType(graph, result.Symbol.ParentId, result.Symbol.Name);
        if (siblings.Length <= 1)
            return result; // only one overload — nothing to surface.

        return new SymbolResolutionResult
        {
            CanonicalId = result.CanonicalId,
            Outcome = result.Outcome,
            Symbol = result.Symbol,
            PrimaryFilePath = result.PrimaryFilePath,
            DeclarationFilePaths = result.DeclarationFilePaths,
            Candidates = result.Candidates,
            Diagnostic = result.Diagnostic,
            Overloads = siblings.Select(s => BuildOverloadInfo(s)).ToArray(),
        };
    }

    private static OverloadInfo BuildOverloadInfo(Symbol sym) => new()
    {
        CanonicalId = sym.Id,
        ParamDisplay = ExtractParamDisplay(sym.Id),
        FilePath = sym.FilePath,
        Line = sym.Line,
    };

    /// <summary>
    /// Extract the parameter display string from a canonical method ID.
    /// <c>method:NS.Type.Name(param1,param2)</c> → <c>param1,param2</c>.
    /// Returns empty for malformed or non-method IDs.
    /// </summary>
    private static string ExtractParamDisplay(string canonicalMethodId)
    {
        var openParen = canonicalMethodId.IndexOf('(');
        var closeParen = canonicalMethodId.LastIndexOf(')');
        if (openParen < 0 || closeParen < 0 || closeParen <= openParen) return "";
        return canonicalMethodId.Substring(openParen + 1, closeParen - openParen - 1);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Internals
    // ────────────────────────────────────────────────────────────────────────

    private static SymbolResolutionResult BuildResolved(
        SemanticGraph graph, string canonicalId, Symbol primary, ResolveOutcome outcome)
    {
        // Non-type symbols degenerate to a single declaration file.
        if (primary.Kind != SymbolKind.Type)
        {
            return new SymbolResolutionResult
            {
                CanonicalId = canonicalId,
                Outcome = outcome,
                Symbol = primary,
                PrimaryFilePath = primary.FilePath,
                DeclarationFilePaths = string.IsNullOrEmpty(primary.FilePath)
                    ? System.Array.Empty<string>()
                    : new[] { primary.FilePath },
            };
        }

        // Partial-type unification: every partial declaration of a type produces
        // a `file:X Contains type:Y` edge during graph build (even when the
        // GraphBuilder dedups the type symbol via last-write-wins). Walk the
        // type's incoming Contains edges to discover all the files.
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

        // Backstop: a malformed graph (or a non-partial type whose Contains
        // edge happens to be missing) shouldn't surface an empty file list
        // when the symbol record itself records a file path.
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

    /// <summary>
    /// INV-RESOLVER-004: deterministic primary file picker for partial types.
    /// Rule 1: filename matches the type name exactly.
    /// Rule 2: filename starts with <c>"&lt;TypeName&gt;."</c> — shortest match wins
    ///         (the bare <c>"Foo.cs"</c> beats <c>"Foo.Init.cs"</c>).
    /// Rule 3: lexicographic first.
    /// Same input → same primary, always.
    /// </summary>
    internal static string ChoosePrimaryFilePath(string typeName, string[] filePaths)
    {
        // Rule 1
        var nameMatch = filePaths.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), typeName,
                StringComparison.OrdinalIgnoreCase));
        if (nameMatch != null) return nameMatch;

        // Rule 2
        var prefixMatches = filePaths
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .StartsWith(typeName + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p).Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (prefixMatches.Length > 0) return prefixMatches[0];

        // Rule 3
        return filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>
    /// Detect the truncated method form: <c>method:NS.Type.Name</c> with no
    /// parens. Returns the type id (<c>type:NS.Type</c>) and the method's
    /// simple name. Returns false for any other shape (canonical with parens,
    /// non-method kind, malformed).
    /// </summary>
    private static bool TryParseMethodWithoutParens(string input, out string typeId, out string methodName)
    {
        typeId = "";
        methodName = "";
        const string prefix = "method:";
        if (!input.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (input.Contains('(')) return false; // already canonical, fast path handled it

        var qualified = input.Substring(prefix.Length);
        var lastDot = qualified.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == qualified.Length - 1) return false;

        var typeName = qualified.Substring(0, lastDot);
        methodName = qualified.Substring(lastDot + 1);
        typeId = "type:" + typeName;
        return true;
    }

    /// <summary>
    /// Find every method symbol whose <see cref="Symbol.ParentId"/> equals
    /// <paramref name="typeId"/> and whose simple <see cref="Symbol.Name"/>
    /// equals <paramref name="methodName"/>. Used by the
    /// <see cref="ResolveOutcome.LenientMethodOverload"/> path.
    /// </summary>
    private static Symbol[] FindOverloadsOnType(SemanticGraph graph, string typeId, string methodName)
    {
        if (graph.GetSymbol(typeId) == null) return System.Array.Empty<Symbol>();

        var overloads = new List<Symbol>(2);
        foreach (int idx in graph.GetOutgoingEdgeIndexes(typeId))
        {
            var edge = graph.Edges[idx];
            if (edge.Kind != EdgeKind.Contains) continue;
            var member = graph.GetSymbol(edge.TargetId);
            if (member == null) continue;
            if (member.Kind != SymbolKind.Method) continue;
            if (!string.Equals(member.Name, methodName, StringComparison.Ordinal)) continue;
            overloads.Add(member);
        }
        return overloads.ToArray();
    }

    /// <summary>
    /// "Looks like a bare short name" = no kind prefix (no <c>:</c>) AND no
    /// namespace dots. Examples that match: <c>MidiLearnManager</c>,
    /// <c>SetPatch</c>. Examples that DON'T: <c>type:Foo</c>,
    /// <c>NS.Type</c>, <c>method:NS.T.M</c>.
    /// </summary>
    private static bool LooksLikeBareShortName(string input)
    {
        if (input.Contains(':')) return false;
        if (input.Contains('.')) return false;
        if (input.Contains('(')) return false;
        return true;
    }
}
