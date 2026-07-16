using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DomainSymbolKind = Lifeblood.Domain.Graph.SymbolKind;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Extracts independent syntax trees from one immutable Roslyn compilation.
/// Workers own their extractors and return indexed results; the workspace
/// analyzer remains the sole writer of <see cref="AnalysisSnapshot"/> state.
/// This preserves deterministic graph publication while allowing expensive
/// semantic binding to use more than one core.
/// </summary>
internal sealed class CompilationTreeExtractor
{
    // Semantic binding is allocation-heavy. A bounded worker set avoids
    // multiplying a large compilation's live working set by the host's full
    // logical-processor count while still removing the single-core bottleneck.
    internal const int MaximumConcurrency = 8;

    public IReadOnlyList<CompilationFileExtraction> Extract(
        CSharpCompilation compilation,
        string projectRoot,
        string moduleName,
        string profileName,
        string? profileTag,
        bool ownsSymbols,
        HashSet<string> knownModuleAssemblies,
        IReadOnlySet<string>? changedFiles = null,
        WorkspaceSourcePathMap? sourcePaths = null)
    {
        sourcePaths ??= WorkspaceSourcePathMap.Create(projectRoot);
        var candidates = compilation.SyntaxTrees
            .Where(tree => !string.IsNullOrEmpty(tree.FilePath)
                           && !tree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .Select(tree => new TreeCandidate(
                tree,
                SyntaxTreePathIdentity.Resolve(sourcePaths, moduleName, tree.FilePath)))
            .Where(candidate => changedFiles == null
                                || candidate.PathIdentity.IsGenerated
                                || changedFiles.Contains(candidate.Tree.FilePath))
            .ToArray();

        if (candidates.Length == 0)
            return Array.Empty<CompilationFileExtraction>();

        var results = new CompilationFileExtraction?[candidates.Length];
        var failures = new Exception?[candidates.Length];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, MaximumConcurrency),
        };

        Parallel.For(0, candidates.Length, options, index =>
        {
            var candidate = candidates[index];
            try
            {
                var tree = candidate.Tree;
                var relPath = candidate.PathIdentity.GraphPath;
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var edgeExtractor = new RoslynEdgeExtractor
                {
                    KnownModuleAssemblies = knownModuleAssemblies,
                };
                var edges = EdgeProfileTagger.Tag(
                    edgeExtractor.Extract(model, root, relPath),
                    profileTag);

                var fileId = SymbolIds.File(relPath);
                Symbol? fileSymbol = null;
                List<Symbol>? symbols = null;
                if (ownsSymbols)
                {
                    fileSymbol = new Symbol
                    {
                        Id = fileId,
                        Name = Path.GetFileName(tree.FilePath),
                        QualifiedName = $"{moduleName}/{relPath}",
                        Kind = DomainSymbolKind.File,
                        FilePath = relPath,
                        ParentId = SymbolIds.Module(moduleName),
                    };
                    symbols = new RoslynSymbolExtractor().Extract(
                        model,
                        root,
                        relPath,
                        fileId);
                }

                results[index] = new CompilationFileExtraction(
                    tree.FilePath,
                    candidate.PathIdentity,
                    fileId,
                    fileSymbol,
                    symbols,
                    edges);
            }
            catch (Exception ex)
            {
                // Capture by source order. Parallel.For would otherwise wrap
                // failures in a scheduler-order AggregateException and lose the
                // stable file provenance required by structured diagnostics.
                failures[index] = ex;
            }
        });

        for (var index = 0; index < failures.Length; index++)
        {
            if (failures[index] is not { } failure) continue;
            throw new WorkspaceAnalysisException(
                "compilation",
                moduleName,
                candidates[index].PathIdentity.GraphPath,
                profileName,
                failedBeforeCompilation: false,
                failure);
        }

        return results.Select(result => result!).ToArray();
    }

    private sealed record TreeCandidate(
        SyntaxTree Tree,
        SyntaxTreePathIdentity PathIdentity);
}

internal sealed record CompilationFileExtraction(
    string TreePath,
    SyntaxTreePathIdentity PathIdentity,
    string FileId,
    Symbol? FileSymbol,
    List<Symbol>? Symbols,
    List<Edge> Edges);
