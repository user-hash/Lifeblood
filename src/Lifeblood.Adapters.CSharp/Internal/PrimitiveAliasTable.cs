namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Single source of truth for C# surface-syntax rewriting of primitive
/// type names. The canonical form used by Lifeblood symbol IDs is the C#
/// alias (<c>string</c>, <c>int</c>, <c>bool</c>, <c>void</c>, …) because
/// <see cref="CanonicalSymbolFormat.ParamType"/> has
/// <see cref="Microsoft.CodeAnalysis.SymbolDisplayMiscellaneousOptions.UseSpecialTypes"/>
/// enabled. User-supplied symbol identifiers, on the other hand, may arrive
/// in either form — users naturally copy/paste from source code
/// (<c>System.String</c>, <c>global::System.Int32</c>) or from IDE tooltips
/// that show the full BCL name.
///
/// This table is consumed by
/// <see cref="Lifeblood.Application.Ports.Right.IUserInputCanonicalizer"/>
/// at step 0 of the resolver pipeline (see INV-RESOLVER-001..004 in
/// CLAUDE.md) to close the gap before any lookup runs.
///
/// Why this table lives in the C# adapter, not in Application or the
/// resolver directly: the rule <c>System.String → string</c> is
/// C#-specific. Python needs its own canonicalizer with its own rules
/// (<c>builtins.int → int</c>). Shoving the C# table into a
/// language-agnostic layer would force every future adapter to work
/// around the cross-language leak.
/// </summary>
internal static class PrimitiveAliasTable
{
    /// <summary>
    /// BCL type full name → C# alias. The table is intentionally exhaustive
    /// over the types that <see cref="Microsoft.CodeAnalysis.SpecialType"/>
    /// represents, so a user who writes <c>System.Byte</c> or
    /// <c>System.UInt64</c> lands on the same canonical ID as
    /// <see cref="CanonicalSymbolFormat.ParamType"/> produces during
    /// extraction. Entries for <c>System.IntPtr</c> / <c>System.UIntPtr</c>
    /// use their C# 9+ aliases only when they would also appear that way in
    /// canonical ID output; until that's verified, keep them out of the
    /// table so we never rewrite to a form the canonical formatter doesn't
    /// itself emit.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["System.String"] = "string",
            ["System.Boolean"] = "bool",
            ["System.Byte"] = "byte",
            ["System.SByte"] = "sbyte",
            ["System.Int16"] = "short",
            ["System.UInt16"] = "ushort",
            ["System.Int32"] = "int",
            ["System.UInt32"] = "uint",
            ["System.Int64"] = "long",
            ["System.UInt64"] = "ulong",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Decimal"] = "decimal",
            ["System.Char"] = "char",
            ["System.Object"] = "object",
            ["System.Void"] = "void",
        };

    /// <summary>
    /// Rewrite every occurrence of a BCL primitive type name in the input
    /// symbol identifier to its C# alias. The operation is token-aware: it
    /// ONLY rewrites occurrences that are complete identifier tokens (i.e.
    /// bounded by delimiters in the symbol ID grammar: <c>(</c>, <c>)</c>,
    /// <c>,</c>, <c>.</c>, <c>&lt;</c>, <c>&gt;</c>, <c>[</c>, <c>]</c>,
    /// <c>:</c>, or start/end of string). A user type named
    /// <c>MyApp.System.StringBuilder</c> is never corrupted because the
    /// <c>System.String</c> prefix is followed by <c>Builder</c>, not a
    /// delimiter, so no rewrite fires.
    ///
    /// Also strips the <c>global::</c> prefix anywhere it appears, for the
    /// same reason: canonical IDs never use <c>global::</c> but users may
    /// paste it in from generated code.
    ///
    /// Idempotent: running the function twice on its own output produces
    /// the same string.
    /// </summary>
    public static string Rewrite(string symbolId)
    {
        if (string.IsNullOrEmpty(symbolId)) return symbolId;

        var working = symbolId;
        if (working.Contains("global::", StringComparison.Ordinal))
            working = working.Replace("global::", "", StringComparison.Ordinal);

        // Fast path: nothing to do if the input doesn't mention "System."
        if (!working.Contains("System.", StringComparison.Ordinal))
            return working;

        var result = new System.Text.StringBuilder(working.Length);
        int i = 0;
        while (i < working.Length)
        {
            // Identify start-of-token positions: either index 0 or preceded
            // by a delimiter.
            if (i == 0 || IsDelimiter(working[i - 1]))
            {
                var matched = TryMatchAlias(working, i, out var aliasLength, out var alias);
                if (matched)
                {
                    // Confirm end-of-token: the character after the match
                    // must also be a delimiter (or end of string) — otherwise
                    // we'd corrupt a longer identifier that happens to start
                    // with the BCL name.
                    var after = i + aliasLength;
                    if (after == working.Length || IsDelimiter(working[after]))
                    {
                        result.Append(alias);
                        i = after;
                        continue;
                    }
                }
            }
            result.Append(working[i]);
            i++;
        }
        return result.ToString();
    }

    private static bool TryMatchAlias(string input, int start, out int matchedLength, out string alias)
    {
        foreach (var (bclName, bclAlias) in Aliases)
        {
            if (start + bclName.Length > input.Length) continue;
            if (string.CompareOrdinal(input, start, bclName, 0, bclName.Length) != 0) continue;
            matchedLength = bclName.Length;
            alias = bclAlias;
            return true;
        }
        matchedLength = 0;
        alias = "";
        return false;
    }

    private static bool IsDelimiter(char c) => c switch
    {
        '(' or ')' or ',' or '.' or '<' or '>' or '[' or ']' or ':' or ' ' or '\t' => true,
        _ => false,
    };
}
