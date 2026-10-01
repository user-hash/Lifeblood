using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.PathClassification;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lifeblood.Adapters.CSharp;

/// <summary>Bounded exact-body grouping. Every token bucket dies with the request.</summary>
internal sealed class RoslynRepeatedCodeProvider(
    IReadOnlyDictionary<string, CSharpCompilation> compilations,
    WorkspaceSourcePathMap sourcePaths,
    string profile,
    IReadOnlyList<string> availableProfiles,
    CanonicalSymbolIdVersion idVersion)
{
    private readonly record struct TokenSpelling(int Kind, string Text);
    private sealed class Bucket(string hash, TokenSpelling[] tokens, RepeatedCodeOccurrence first)
    {
        internal readonly string Hash = hash;
        internal readonly TokenSpelling[] Tokens = tokens;
        internal readonly List<RepeatedCodeOccurrence> Members = new() { first };
        internal int Count = 1;
    }

    internal RepeatedCodeReport Find(RepeatedCodeQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (query.ProfileScope != null && !string.Equals(query.ProfileScope, profile, StringComparison.Ordinal))
            throw new ArgumentException($"Repeated-code evidence retains only profile '{profile}'. Re-analyze with the requested profile first.", nameof(query));

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = query.FilePaths?.Select(path => path.Replace('\\', '/')).ToHashSet(pathComparer);
        var byHash = new Dictionary<string, List<Bucket>>(StringComparer.Ordinal);
        var buckets = new List<Bucket>();
        var seen = new HashSet<(string Path, int Start, int Length)>();
        var limits = new List<string>();
        var scannedFiles = 0;
        var scannedNodes = 0;
        var observedBodies = 0;
        var comparedBodies = 0;
        var scannedTokens = 0;
        var sourceCharacters = 0;
        var excludedFiles = 0;
        var malformed = 0;
        var stop = false;

        foreach (var (module, compilation) in compilations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.ModuleScope != null && !string.Equals(query.ModuleScope, module, StringComparison.Ordinal)) continue;
            foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = SyntaxTreePathIdentity.Resolve(sourcePaths, module, tree.FilePath);
                var path = identity.GraphPath;
                if (files is { Count: > 0 } && !files.Contains(path) && !files.Contains(tree.FilePath.Replace('\\', '/'))) continue;
                if ((query.ExcludeGenerated && (identity.IsGenerated || PathBucketClassifier.IsGenerated(path)))
                    || (query.ExcludeTests && PathBucketClassifier.IsTest(path)))
                {
                    excludedFiles++;
                    continue;
                }
                if (scannedFiles >= query.MaxFiles) { limits.Add(nameof(query.MaxFiles)); stop = true; break; }
                var characters = tree.GetText(cancellationToken).Length;
                if (characters > query.MaxSourceCharacters - sourceCharacters)
                { limits.Add(nameof(query.MaxSourceCharacters)); stop = true; break; }
                scannedFiles++;
                sourceCharacters += characters;
                var root = tree.GetRoot(cancellationToken);
                SemanticModel? model = null;
                foreach (var node in root.DescendantNodes(descendIntoChildren: node => Body(node) == null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scannedNodes >= query.MaxNodes) { limits.Add(nameof(query.MaxNodes)); stop = true; break; }
                    scannedNodes++;
                    var body = Body(node);
                    var physicalKey = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                    if (body == null || !seen.Add((physicalKey, body.SpanStart, body.Span.Length))) continue;
                    if (observedBodies >= query.MaxBodies) { limits.Add(nameof(query.MaxBodies)); stop = true; break; }
                    observedBodies++;
                    if (body.ContainsDiagnostics) { malformed++; continue; }
                    var tokens = new List<TokenSpelling>();
                    foreach (var token in body.DescendantTokens(descendIntoTrivia: false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (scannedTokens >= query.MaxTokens) { limits.Add(nameof(query.MaxTokens)); stop = true; break; }
                        scannedTokens++;
                        tokens.Add(new TokenSpelling(token.RawKind, token.Text));
                    }
                    if (stop) break;
                    if (tokens.Count < query.MinTokens) continue;
                    model ??= compilation.GetSemanticModel(tree);
                    var symbol = model.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol == null) { malformed++; continue; }
                    var span = body.GetLocation().GetLineSpan().Span;
                    var occurrence = new RepeatedCodeOccurrence(module,
                        CanonicalSymbolFormat.BuildSymbolId(symbol.OriginalDefinition, idVersion),
                        BodyKind(node, symbol),
                        new OperationSourceSpan { FilePath = path, Line = span.Start.Line + 1, Column = span.Start.Character + 1,
                            EndLine = span.End.Line + 1, EndColumn = span.End.Character + 1 });
                    var sequence = tokens.ToArray();
                    var hash = Hash(sequence, cancellationToken);
                    if (!byHash.TryGetValue(hash, out var candidates)) byHash[hash] = candidates = new();
                    var bucket = candidates.FirstOrDefault(candidate => candidate.Tokens.AsSpan().SequenceEqual(sequence));
                    if (bucket == null)
                    {
                        bucket = new Bucket(hash, sequence, occurrence);
                        candidates.Add(bucket);
                        buckets.Add(bucket);
                    }
                    else
                    {
                        bucket.Count++;
                        if (bucket.Members.Count < query.MaxMembersPerGroup) bucket.Members.Add(occurrence);
                    }
                    comparedBodies++;
                }
                if (stop) break;
            }
            if (stop) break;
        }
        var matched = buckets.Where(bucket => bucket.Count >= 2).ToArray();
        if (matched.Length > query.MaxGroups) limits.Add(nameof(query.MaxGroups));
        if (matched.Any(bucket => bucket.Count > bucket.Members.Count)) limits.Add(nameof(query.MaxMembersPerGroup));
        return new RepeatedCodeReport
        {
            ProfileScope = profile, AvailableProfiles = availableProfiles.ToArray(), Scope = query,
            ScannedFiles = scannedFiles, ScannedNodes = scannedNodes, ObservedBodies = observedBodies,
            ComparedBodies = comparedBodies, ScannedTokens = scannedTokens, SourceCharacters = sourceCharacters,
            ExcludedFiles = excludedFiles, SkippedMalformedBodies = malformed,
            GroupCount = matched.Length, Truncated = limits.Count > 0, ReachedLimits = limits.ToArray(),
            Groups = matched.Take(query.MaxGroups).Select(bucket => new RepeatedCodeGroup(
                "repeated_body_" + bucket.Hash, bucket.Tokens.Length, bucket.Count,
                bucket.Count > bucket.Members.Count, bucket.Members.ToArray())).ToArray(),
            Limitations = new[] {
                "Matches compare complete executable token bodies, preserving token kinds and spelling. Trivia is ignored. Equal tokens do not establish equal bindings, behavior, ownership or safe deletion.",
                "Only the retained profile's analyzed trees are scanned. Exclusions and input/output limits reduce coverage; counts describe observed scope.",
                "Methods, constructors, operators, destructors, accessors and expression-bodied properties/indexers are supported. Constructor initializers and signatures are outside the body comparison. Top-level code and abstract/extern declarations are not compared.",
                "Local functions and lambdas remain inside their enclosing body, never overlapping standalone occurrences. Linked physical bodies are reported once under the first selected module.",
            },
        };
    }

    private static SyntaxNode? Body(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
        AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression,
        PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
        IndexerDeclarationSyntax indexer => indexer.ExpressionBody?.Expression,
        _ => null,
    };

    private static string BodyKind(SyntaxNode node, ISymbol symbol) => node is AccessorDeclarationSyntax ? "Accessor"
        : symbol is IMethodSymbol method ? method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "Constructor",
            MethodKind.UserDefinedOperator or MethodKind.Conversion => "Operator",
            MethodKind.Destructor => "Destructor",
            _ => "Method",
        } : symbol.Kind.ToString();

    private static string Hash(TokenSpelling[] tokens, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = Encoding.UTF8.GetBytes(token.Text);
            BinaryPrimitives.WriteInt32LittleEndian(header, token.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], text.Length);
            hash.AppendData(header);
            hash.AppendData(text);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
