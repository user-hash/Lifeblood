using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// The single C#-adapter authority for mapping a requested source path to
/// retained compilation membership. Exact normalized paths outrank suffix
/// matches; multiple matches fail closed with deterministic candidates.
/// INV-COMPILATION-FILE-OWNERSHIP-001.
/// </summary>
internal static class CompilationFileOwnershipResolver
{
    internal static CompilationFileOwner Resolve(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        string filePath,
        string? moduleName)
    {
        CSharpCompilation? pinned = null;
        if (moduleName != null && !compilations.TryGetValue(moduleName, out pinned))
        {
            return Missing(CompilationFileOwnershipOutcome.ModuleNotFound);
        }

        IEnumerable<KeyValuePair<string, CSharpCompilation>> candidates = moduleName == null
            ? compilations
            : new[] { new KeyValuePair<string, CSharpCompilation>(moduleName, pinned!) };
        var requested = Normalize(filePath);
        var matches = new List<CompilationFileMatch>();

        foreach (var pair in candidates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var tree in pair.Value.SyntaxTrees)
            {
                var treePath = Normalize(tree.FilePath);
                if (treePath.Length == 0) continue;
                var rank = PathEquals(treePath, requested)
                    ? 2
                    : IsBoundarySuffix(treePath, requested) || IsBoundarySuffix(requested, treePath)
                        ? 1
                        : 0;
                if (rank > 0)
                    matches.Add(new CompilationFileMatch(pair.Key, pair.Value, tree, treePath, rank));
            }
        }

        var bestRank = matches.Count == 0 ? 0 : matches.Max(match => match.Rank);
        var best = matches
            .Where(match => match.Rank == bestRank)
            .OrderBy(match => match.Module, StringComparer.Ordinal)
            .ThenBy(match => match.NormalizedPath, PathComparer)
            .ToArray();
        if (best.Length == 0)
        {
            return Missing(moduleName == null
                ? CompilationFileOwnershipOutcome.NotFound
                : CompilationFileOwnershipOutcome.NotInModule);
        }

        var ownership = new CompilationFileOwnership
        {
            Outcome = best.Length == 1
                ? CompilationFileOwnershipOutcome.Unique
                : CompilationFileOwnershipOutcome.Ambiguous,
            ResolvedModule = best.Length == 1 ? best[0].Module : "",
            CandidateModules = best
                .Select(match => match.Module)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            CandidateFilePaths = best
                .Select(match => match.Tree.FilePath ?? "")
                .Distinct(PathComparer)
                .ToArray(),
        };
        return best.Length == 1
            ? new CompilationFileOwner(ownership, best[0].Compilation, best[0].Tree)
            : new CompilationFileOwner(ownership, null, null);
    }

    private static CompilationFileOwner Missing(CompilationFileOwnershipOutcome outcome)
        => new(new CompilationFileOwnership { Outcome = outcome }, null, null);

    private static string Normalize(string? path)
        => (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');

    private static bool PathEquals(string left, string right)
        => PathComparer.Equals(left, right);

    private static bool IsBoundarySuffix(string path, string suffix)
        => suffix.Length > 0
           && path.Length > suffix.Length
           && path.EndsWith("/" + suffix, PathComparison);

    private static StringComparer PathComparer { get; }
        = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; }
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record CompilationFileMatch(
        string Module,
        CSharpCompilation Compilation,
        SyntaxTree Tree,
        string NormalizedPath,
        int Rank);
}

internal sealed record CompilationFileOwner(
    CompilationFileOwnership Ownership,
    CSharpCompilation? Compilation,
    SyntaxTree? Tree);
