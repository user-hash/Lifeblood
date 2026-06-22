using System.Text;
using System.Text.RegularExpressions;

namespace Lifeblood.Domain.PathClassification;

/// <summary>
/// Shared anchored path-glob matcher for analysis-scope and triage filters.
/// Grammar is deliberately small and byte-stable: <c>*</c> matches any run
/// of characters, including <c>/</c>; <c>?</c> matches one character; every
/// other character is escaped literally. Globs match the full normalized
/// POSIX path. INV-PATH-GLOB-001.
/// </summary>
public static class PathGlobMatcher
{
    public static Regex[] Compile(string[]? globs)
    {
        if (globs == null) return Array.Empty<Regex>();

        var compiled = new List<Regex>();
        foreach (var glob in globs)
        {
            if (string.IsNullOrWhiteSpace(glob)) continue;

            var sb = new StringBuilder("^");
            foreach (var ch in glob.Replace('\\', '/'))
            {
                sb.Append(ch switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString()),
                });
            }
            sb.Append('$');
            compiled.Add(new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        return compiled.ToArray();
    }

    public static bool MatchesAny(IReadOnlyList<Regex> globs, string? filePath)
    {
        if (globs.Count == 0 || string.IsNullOrEmpty(filePath)) return false;

        var normalized = filePath.Replace('\\', '/');
        foreach (var rx in globs)
        {
            if (rx.IsMatch(normalized)) return true;
        }

        return false;
    }
}
