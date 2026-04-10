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

        // Rule 1: exact canonical match. Fast path for callers that already
        // have a fully-qualified ID (e.g., copied from a previous tool result).
        var exact = graph.GetSymbol(userInput);
        if (exact != null)
            return BuildResolved(graph, userInput, exact, ResolveOutcome.ExactMatch);

        // Rule 2: truncated method form. Pattern: "method:NS.Type.Name" with
        // no parameter parens. We try to find every method on NS.Type whose
        // simple name matches and either return the unique overload or
        // surface ambiguity.
        if (TryParseMethodWithoutParens(userInput, out var typeId, out var methodName))
        {
            var overloads = FindOverloadsOnType(graph, typeId, methodName);
            if (overloads.Length == 1)
                return BuildResolved(graph, overloads[0].Id, overloads[0],
                    ResolveOutcome.LenientMethodOverload);
            if (overloads.Length > 1)
            {
                return new SymbolResolutionResult
                {
                    Outcome = ResolveOutcome.AmbiguousMethodOverload,
                    Candidates = overloads.Select(o => o.Id).ToArray(),
                    Diagnostic = $"Method '{methodName}' on '{typeId}' has " +
                                 $"{overloads.Length} overloads. Pick one from Candidates.",
                };
            }
            // Fall through to short-name and not-found rules.
        }

        // Rule 3: bare short name. No kind prefix and no namespace dots —
        // user is shorthand-querying, e.g. "MidiLearnManager".
        if (LooksLikeBareShortName(userInput))
        {
            var matches = graph.FindByShortName(userInput);
            if (matches.Count == 1)
                return BuildResolved(graph, matches[0].Id, matches[0],
                    ResolveOutcome.ShortNameUnique);
            if (matches.Count > 1)
            {
                return new SymbolResolutionResult
                {
                    Outcome = ResolveOutcome.AmbiguousShortName,
                    Candidates = matches.Select(s => s.Id).ToArray(),
                    Diagnostic = $"Short name '{userInput}' matches {matches.Count} " +
                                 "symbols across different namespaces. Pick one from Candidates " +
                                 "or query lifeblood_resolve_short_name for details.",
                };
            }
        }

        return new SymbolResolutionResult
        {
            Outcome = ResolveOutcome.NotFound,
            Diagnostic = $"Symbol not found: {userInput}. " +
                         "Tried exact canonical match, lenient method overload, and short-name lookup. " +
                         "Use lifeblood_resolve_short_name to discover candidate IDs.",
        };
    }

    public ShortNameMatch[] ResolveShortName(SemanticGraph graph, string shortName)
    {
        if (string.IsNullOrWhiteSpace(shortName))
            return System.Array.Empty<ShortNameMatch>();

        return graph.FindByShortName(shortName)
            .Select(s => new ShortNameMatch
            {
                CanonicalId = s.Id,
                FilePath = s.FilePath,
                Kind = s.Kind.ToString(),
            })
            .ToArray();
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
