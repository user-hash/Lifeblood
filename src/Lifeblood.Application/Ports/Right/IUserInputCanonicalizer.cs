namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Step 0 of symbol resolution (see <see cref="ISymbolResolver.Resolve"/>).
/// Rewrites user-supplied symbol identifiers into the canonical grammar the
/// graph uses for storage, BEFORE any lookup attempt.
///
/// The architectural reason this exists as a port and not a helper inside
/// the resolver: user-supplied identifiers live in a DIFFERENT GRAMMAR from
/// the one the graph stores. For C#, users naturally write surface-language
/// forms like <c>System.String</c> or <c>System.Int32</c>, but the canonical
/// ID format uses C# aliases (<c>string</c>, <c>int</c>) produced by
/// <c>Lifeblood.Adapters.CSharp.Internal.CanonicalSymbolFormat</c>. Those two
/// surface forms refer to the same type but do not compare equal as strings.
///
/// Historically the resolver papered over this by adding alias-rewrite
/// retries as a fallback after the exact-match step had already failed,
/// which produced confusing intermediate diagnostics and left the resolver
/// with a multi-step lookup order that grew every time a new grammar
/// mismatch surfaced. The canonicalization port replaces that pattern: the
/// canonicalizer runs once at step 0, every subsequent step operates on
/// the canonical form, and every diagnostic, every <c>Candidates[]</c>
/// entry, and every log line references the canonical form (never the
/// user's raw input).
///
/// Each language adapter supplies its own canonicalizer. C# rewrites
/// primitive-type aliases. Python will rewrite <c>int</c> vs
/// <c>builtins.int</c>. TypeScript will rewrite <c>Array&lt;T&gt;</c> vs
/// <c>T[]</c>. The port decouples these language-specific concerns from the
/// language-agnostic resolver pipeline.
///
/// Added 2026-04-11 as part of Phase 3 of the improvement-master plan.
/// See <c>.claude/plans/improvement-master-2026-04-11.md</c> §Part 4 B3.
/// </summary>
public interface IUserInputCanonicalizer
{
    /// <summary>
    /// Rewrite a user-supplied symbol identifier into the canonical grammar.
    /// Invariants on the returned value:
    /// <list type="bullet">
    ///   <item>Same canonical form if the input is already canonical (idempotent).</item>
    ///   <item>Never null. On an unrecognizable input, the original string is returned unchanged — the resolver will route it through normal lookup and, if it misses, surface NotFound with the canonicalized (= original) form.</item>
    ///   <item>Never throws. Malformed inputs produce unchanged output, not exceptions.</item>
    /// </list>
    /// </summary>
    string Canonicalize(string userInput);
}

/// <summary>
/// A no-op canonicalizer used when no language adapter is wired in. Returns
/// the input unchanged. The resolver accepts this as a valid dependency so
/// that tests and non-C# workspaces can construct a resolver without having
/// to stand up a full adapter. INV-RESOLVER-001 still holds: the resolver
/// always routes through a canonicalizer — this one just happens to be the
/// identity function.
/// </summary>
public sealed class IdentityUserInputCanonicalizer : IUserInputCanonicalizer
{
    public static readonly IdentityUserInputCanonicalizer Instance = new();
    public string Canonicalize(string userInput) => userInput;
}
