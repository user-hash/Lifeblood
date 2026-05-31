using System.Text.RegularExpressions;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-DOC-LINK-INTEGRITY-001. Every relative Markdown link in a tracked doc must
/// resolve to a file or directory that exists. Deleting a doc without updating its
/// inbound links is a release blocker; green code tests do not catch it, so this
/// ratchet does. Local links only — external (http/https/mailto) and pure
/// in-page anchors (#section) are out of scope.
/// </summary>
public class DocLinkIntegrityTests
{
    // ](target) or ](target "title") — capture the target token up to whitespace or ).
    private static readonly Regex LinkPattern = new(@"\]\(\s*(<[^>]+>|[^)\s]+)", RegexOptions.Compiled);

    private static readonly string[] SkipDirSegments =
    {
        ".git", "bin", "obj", "node_modules", "artifacts", "publish-staging",
        "dist", ".claude", "TestResults", ".vs",
    };

    [Fact]
    public void TrackedMarkdown_RelativeLinks_ResolveToExistingTargets()
    {
        var root = FindRepoRoot();
        var broken = new List<string>();

        foreach (var md in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            if (IsUnderSkippedDir(md, root)) continue;

            var dir = Path.GetDirectoryName(md)!;
            var text = File.ReadAllText(md);

            foreach (Match m in LinkPattern.Matches(text))
            {
                var raw = m.Groups[1].Value.Trim().Trim('<', '>');
                if (!IsLocalRelativeLink(raw)) continue;

                // Strip in-page anchor + query, URL-decode spaces.
                var pathPart = raw.Split('#', 2)[0].Split('?', 2)[0].Replace("%20", " ");
                if (pathPart.Length == 0) continue; // pure anchor like (#foo)

                var resolved = Path.GetFullPath(Path.Combine(dir, pathPart));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    var rel = Path.GetRelativePath(root, md).Replace('\\', '/');
                    broken.Add($"{rel} -> {raw}");
                }
            }
        }

        Assert.True(
            broken.Count == 0,
            $"INV-DOC-LINK-INTEGRITY-001: {broken.Count} tracked Markdown link(s) point at a missing target:\n"
            + string.Join("\n", broken.OrderBy(s => s)));
    }

    private static bool IsLocalRelativeLink(string target)
    {
        if (target.Length == 0) return false;
        if (target.StartsWith('#')) return false;
        if (target.StartsWith("http://") || target.StartsWith("https://")) return false;
        if (target.StartsWith("mailto:") || target.StartsWith("tel:")) return false;
        // Any URI scheme (a ':' before the first '/') means external — skip it.
        var slash = target.IndexOf('/');
        var colon = target.IndexOf(':');
        if (colon >= 0 && (slash < 0 || colon < slash)) return false;
        return true;
    }

    private static bool IsUnderSkippedDir(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => SkipDirSegments.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Lifeblood.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate Lifeblood.sln from test base directory.");
    }
}
