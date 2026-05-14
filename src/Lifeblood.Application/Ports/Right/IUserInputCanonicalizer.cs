namespace Lifeblood.Application.Ports.Right;

/// <summary>
/// Step 0 of symbol resolution (see <see cref="ISymbolResolver.Resolve"/>).
/// Rewrites user-supplied symbol identifiers into the canonical grammar the
/// graph uses for storage, BEFORE any lookup attempt.
///
/// Exists as a port because user-supplied identifiers live in a DIFFERENT
/// GRAMMAR from the one the graph stores. For C#, users naturally write
/// surface-language forms like <c>System.String</c> or <c>System.Int32</c>,
/// but the canonical ID format uses C# aliases (<c>string</c>, <c>int</c>)
/// produced by <c>Lifeblood.Adapters.CSharp.Internal.CanonicalSymbolFormat</c>.
/// Those two surface forms refer to the same type but do not compare equal
/// as strings. The canonicalizer runs once at step 0, every subsequent step
/// operates on the canonical form, and every diagnostic / <c>Candidates[]</c>
/// entry / log line references the canonical form (never the user's raw input).
///
/// Each language adapter supplies its own canonicalizer. C# rewrites
/// primitive-type aliases. Python rewrites <c>int</c> vs <c>builtins.int</c>.
/// TypeScript rewrites <c>Array&lt;T&gt;</c> vs <c>T[]</c>. The port decouples
/// these language-specific concerns from the language-agnostic resolver pipeline.
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
