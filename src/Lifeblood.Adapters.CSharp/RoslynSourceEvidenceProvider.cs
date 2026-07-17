using System.Security.Cryptography;
using System.Text;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// One-pass Roslyn-to-neutral lexical-evidence adapter. It reuses the retained
/// syntax trees and semantic models, streams only caller-selected terms, and
/// retains no facts or compiler objects beyond the request.
/// </summary>
internal sealed class RoslynSourceEvidenceProvider
{
    private const int MaximumTextLength = 512;

    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    private readonly string _profileScope;
    private readonly string[] _availableProfiles;

    internal RoslynSourceEvidenceProvider(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        string profileScope,
        IReadOnlyList<string> availableProfiles)
    {
        _compilations = compilations;
        _profileScope = profileScope;
        _availableProfiles = availableProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal SourceEvidenceScanReceipt Scan(
        SourceEvidenceQuery query,
        Func<SourceEvidenceFact, bool> consume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        if (query.MaxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "maxFacts must be greater than zero.");
        if (query.ProfileScope != null
            && !string.Equals(query.ProfileScope, _profileScope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Requested profile '{query.ProfileScope}' is not the retained source-evidence profile '{_profileScope}'. " +
                $"Available profiles: {string.Join(", ", _availableProfiles)}.",
                nameof(query));
        }

        var terms = query.SearchTerms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            throw new ArgumentException("At least one non-empty source-evidence search term is required.", nameof(query));

        var kinds = query.IncludeKinds is { Count: > 0 }
            ? query.IncludeKinds
                .Where(kind => !string.IsNullOrWhiteSpace(kind))
                .Select(kind => kind.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal)
            : SourceEvidenceKind.All.ToHashSet(StringComparer.Ordinal);
        var unknownKinds = kinds.Where(kind => !SourceEvidenceKind.All.Contains(kind, StringComparer.Ordinal)).ToArray();
        if (unknownKinds.Length > 0)
            throw new ArgumentException("Unknown source-evidence kinds: " + string.Join(", ", unknownKinds), nameof(query));

        var fileFilter = query.FilePaths is { Count: > 0 }
            ? query.FilePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .ToHashSet(PathComparer)
            : null;
        var scannedTreePaths = new HashSet<string>(PathComparer);
        var scannedModules = 0;
        var scannedFiles = 0;
        var observedEvidence = 0;
        var emittedFacts = 0;
        var truncated = false;
        var stoppedByConsumer = false;
        var stop = false;

        foreach (var (moduleName, compilation) in _compilations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (query.ModuleScope != null
                && !string.Equals(query.ModuleScope, moduleName, StringComparison.Ordinal))
                continue;

            scannedModules++;
            foreach (var tree in compilation.SyntaxTrees.OrderBy(candidate => candidate.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = NormalizePath(tree.FilePath);
                if (!scannedTreePaths.Add(filePath)) continue;
                if (fileFilter is { Count: > 0 } && !MatchesPath(fileFilter, filePath)) continue;

                scannedFiles++;
                var root = tree.GetRoot(cancellationToken);
                var model = compilation.GetSemanticModel(tree);

                foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
                {
                    var kind = ClassifyTrivia(trivia);
                    if (kind == null || !kinds.Contains(kind)) continue;
                    observedEvidence++;
                    if (!EmitMatches(
                            trivia.ToFullString(),
                            kind,
                            moduleName,
                            filePath,
                            trivia.GetLocation(),
                            FindContainingSymbolId(model, trivia.Token.Parent, trivia.SpanStart, cancellationToken),
                            terms,
                            query.MaxFacts,
                            consume,
                            ref emittedFacts,
                            ref truncated,
                            ref stoppedByConsumer))
                    {
                        stop = true;
                        break;
                    }
                }

                if (!stop && kinds.Contains(SourceEvidenceKind.StringLiteral))
                {
                    foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
                    {
                        if (!token.IsKind(SyntaxKind.StringLiteralToken)
                            && !token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                            continue;
                        observedEvidence++;
                        if (!EmitMatches(
                                token.ValueText,
                                SourceEvidenceKind.StringLiteral,
                                moduleName,
                                filePath,
                                token.GetLocation(),
                                FindContainingSymbolId(model, token.Parent, token.SpanStart, cancellationToken),
                                terms,
                                query.MaxFacts,
                                consume,
                                ref emittedFacts,
                                ref truncated,
                                ref stoppedByConsumer))
                        {
                            stop = true;
                            break;
                        }
                    }
                }

                if (stop) break;
            }

            if (stop) break;
        }

        return new SourceEvidenceScanReceipt
        {
            Status = SourceEvidenceScanStatus.Completed,
            ProfileScope = _profileScope,
            AvailableProfiles = _availableProfiles,
            ExecutionMode = SourceEvidenceExecutionMode.RetainedSyntaxTrees,
            InputIdentityVerifiedAtStart = true,
            AdditionalSemanticBaseCount = 0,
            ScannedModuleCount = scannedModules,
            ScannedFileCount = scannedFiles,
            ObservedEvidenceCount = observedEvidence,
            EmittedFactCount = emittedFacts,
            Truncated = truncated,
            StoppedByConsumer = stoppedByConsumer,
            Limitations = new[]
            {
                "Lexical evidence is limited to comments, XML documentation, and string text in the retained profile's analyzed C# syntax trees; generated, skipped, or non-C# sources are outside this pass.",
                "Term matching is ordinal case-insensitive substring matching supplied by the caller; Lifeblood does not infer synonyms or decide whether matched prose is correct.",
            },
        };
    }

    private bool EmitMatches(
        string text,
        string kind,
        string moduleName,
        string filePath,
        Location location,
        string? containingSymbolId,
        string[] terms,
        int maxFacts,
        Func<SourceEvidenceFact, bool> consume,
        ref int emittedFacts,
        ref bool truncated,
        ref bool stoppedByConsumer)
    {
        foreach (var term in terms)
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            if (emittedFacts >= maxFacts)
            {
                truncated = true;
                return false;
            }

            var source = BuildSourceSpan(filePath, location);
            var fact = new SourceEvidenceFact
            {
                Id = BuildFactId(kind, term, source),
                Kind = kind,
                ModuleName = moduleName,
                ProfileScope = _profileScope,
                MatchedTerm = term,
                Text = BoundText(text),
                ContainingSymbolId = containingSymbolId,
                Source = source,
            };
            emittedFacts++;
            if (consume(fact)) continue;
            stoppedByConsumer = true;
            return false;
        }
        return true;
    }

    private static string? ClassifyTrivia(SyntaxTrivia trivia)
    {
        if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            return SourceEvidenceKind.XmlDocumentation;
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            return SourceEvidenceKind.Comment;
        return null;
    }

    private static string? FindContainingSymbolId(
        SemanticModel model,
        SyntaxNode? node,
        int position,
        CancellationToken cancellationToken)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            var declared = model.GetDeclaredSymbol(current, cancellationToken);
            if (declared != null && IsGraphContainingSymbol(declared))
                return CanonicalSymbolFormat.BuildSymbolId(declared.OriginalDefinition);
        }

        var enclosing = model.GetEnclosingSymbol(position, cancellationToken);
        while (enclosing != null && !IsGraphContainingSymbol(enclosing))
            enclosing = enclosing.ContainingSymbol;
        return enclosing == null
            ? null
            : CanonicalSymbolFormat.BuildSymbolId(enclosing.OriginalDefinition);
    }

    private static bool IsGraphContainingSymbol(ISymbol? symbol)
        => symbol is INamespaceSymbol
            or INamedTypeSymbol
            or IMethodSymbol
            or IFieldSymbol
            or IPropertySymbol
            or IEventSymbol;

    private static OperationSourceSpan BuildSourceSpan(string filePath, Location location)
    {
        var span = location.GetLineSpan().Span;
        return new OperationSourceSpan
        {
            FilePath = filePath,
            Line = span.Start.Line + 1,
            Column = span.Start.Character + 1,
            EndLine = span.End.Line + 1,
            EndColumn = span.End.Character + 1,
        };
    }

    private static string BuildFactId(string kind, string term, OperationSourceSpan source)
    {
        var identity = $"{kind}\n{term}\n{source.FilePath}\n{source.Line}\n{source.Column}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "source_evidence_" + Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string BoundText(string text)
    {
        var normalized = string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= MaximumTextLength
            ? normalized
            : normalized[..MaximumTextLength] + "…";
    }

    private static bool MatchesPath(HashSet<string> filter, string filePath)
        => filter.Contains(filePath)
           || filter.Any(candidate => filePath.EndsWith('/' + candidate, PathComparison));

    private static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/');

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
