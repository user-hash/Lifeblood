using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DomainReferenceLocation = Lifeblood.Domain.Results.ReferenceLocation;
using DomainReferenceKind = Lifeblood.Domain.Results.ReferenceKind;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Request-local reference scan using canonical symbol IDs across source and
/// metadata. Owns syntax occurrence normalization and containing-member lookup;
/// user-defined conversions consume the shared operation-fact authority.
/// </summary>
internal sealed class RoslynFindReferencesService
{
    private readonly IRoslynLookup _lookup;
    private readonly RoslynOperationFactProvider _operationFacts;

    internal RoslynFindReferencesService(IRoslynLookup lookup, RoslynOperationFactProvider operationFacts)
    {
        _lookup = lookup;
        _operationFacts = operationFacts;
    }

    internal ReferenceSearchReport FindReferences(FindReferencesRequest request, FindReferencesOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        if (request.SymbolIds.Count is < 1 or > FindReferencesRequest.MaximumTargets)
            throw new ArgumentOutOfRangeException(nameof(request), $"A reference scan requires 1..{FindReferencesRequest.MaximumTargets} targets.");

        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        var orderedTargets = new (string requestedId, Target? target)[request.SymbolIds.Count];
        var admissions = new Dictionary<string, Target?>(StringComparer.Ordinal);
        var symbolIds = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        long idBuilds = 0;
        string Id(ISymbol symbol)
        {
            if (symbolIds.TryGetValue(symbol, out var id)) return id;
            idBuilds++;
            return symbolIds[symbol] = _lookup.BuildSymbolId(symbol);
        }
        for (var i = 0; i < request.SymbolIds.Count; i++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var requested = request.SymbolIds[i];
            if (admissions.TryGetValue(requested, out var admitted))
            {
                orderedTargets[i] = (requested, admitted);
                continue;
            }
            var resolved = _lookup.ResolveFromSource(requested);
            Target? target = null;
            if (resolved != null)
            {
                var id = _lookup.BuildSymbolId(resolved);
                if (!targets.TryGetValue(id, out target))
                    targets.Add(id, target = new Target(id, resolved));
            }
            admissions.Add(requested, target);
            orderedTargets[i] = (requested, target);
        }

        var conversionIds = targets.Values.Where(target => target.IsConversion).Select(target => target.Id).ToArray();
        if (conversionIds.Length > 0)
        {
            // Conversion binding belongs to the existing operation authority.
            // All conversion targets share one traversal, including implicit uses.
            _operationFacts.Scan(new OperationFactQuery
            {
                IncludeKinds = new[] { OperationFactKind.Conversion },
                TargetSymbolIds = conversionIds,
                IncludeImplicit = true,
                MaxFacts = int.MaxValue,
            }, fact =>
            {
                var target = targets[fact.TargetSymbolId!];
                var owner = CompilationFileOwnershipResolver.Resolve(
                    _lookup.Compilations, fact.Source.FilePath, fact.ModuleName);
                if (owner.Tree is not { } tree)
                    throw new InvalidOperationException("A retained operation fact has no unique source tree.");
                var text = tree.GetText(options.CancellationToken);
                var start = text.Lines[fact.Source.Line - 1].Start + fact.Source.Column - 1;
                var end = text.Lines[fact.Source.EndLine - 1].Start + fact.Source.EndColumn - 1;
                var occurrence = new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start);
                if (!target.Seen.Add((tree, start, occurrence.Length, DomainReferenceKind.Usage))) return true;
                var mapped = tree.GetMappedLineSpan(occurrence, options.CancellationToken);
                target.Locations.Add(new DomainReferenceLocation
                {
                    FilePath = mapped.Path,
                    Line = mapped.StartLinePosition.Line + 1,
                    Column = mapped.StartLinePosition.Character + 1,
                    SpanText = text.GetSubText(occurrence).ToString(),
                    ContainingSymbolId = fact.ContainingSymbolId,
                    Kind = DomainReferenceKind.Usage,
                });
                return true;
            }, options.CancellationToken);
        }

        var targetNames = targets.Values.Where(target => !target.IsConversion)
            .Select(target => target.Symbol.Name).ToHashSet(StringComparer.Ordinal);
        var candidateSpellings = new HashSet<string>(targetNames, StringComparer.Ordinal) { "var", "field" };
        foreach (var target in targets.Values)
        {
            if (target.Symbol is INamedTypeSymbol && target.Symbol.Name.EndsWith("Attribute", StringComparison.Ordinal))
                candidateSpellings.Add(target.Symbol.Name[..^"Attribute".Length]);
            if (target.Symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 })
                candidateSpellings.Add(target.Symbol.Name[(target.Symbol.Name.LastIndexOf('.') + 1)..]);
        }
        int treeVisits = 0;
        long nodeVisits = 0;
        long bindings = 0;
        if (targets.Values.Any(target => !target.IsConversion))
        foreach (var compilation in _lookup.Compilations.Values)
        {
            // Include every alias spelling, including global aliases declared
            // in another tree. Binding still establishes the exact target.
            var compilationSpellings = new HashSet<string>(candidateSpellings, StringComparer.Ordinal);
            foreach (var aliasTree in compilation.SyntaxTrees)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                foreach (var directive in aliasTree.GetRoot(options.CancellationToken)
                    .DescendantNodes(node => node is not MemberDeclarationSyntax or BaseNamespaceDeclarationSyntax)
                    .OfType<UsingDirectiveSyntax>())
                    if (directive.Alias is { } alias) compilationSpellings.Add(alias.Name.Identifier.ValueText);
            }
            foreach (var tree in compilation.SyntaxTrees)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                treeVisits++;
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(options.CancellationToken);
                var sourceText = tree.GetText(options.CancellationToken);
                foreach (var node in root.DescendantNodes())
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    nodeVisits++;
                    if (node is SimpleNameSyntax name && !compilationSpellings.Contains(name.Identifier.ValueText)) continue;
                    bindings++;
                    var resolved = model.GetSymbolInfo(node, options.CancellationToken).Symbol;
                    if (resolved == null) continue;
                    // Canonicalize only compiler-bound candidates with a target name.
                    // Accessor IDs address their associated property/event owner.
                    var matchName = resolved is IMethodSymbol { AssociatedSymbol: { } owner }
                        ? owner.Name : resolved.Name;
                    if (!targetNames.Contains(matchName)) continue;
                    if (!targets.TryGetValue(Id(resolved), out var target)
                        && !targets.TryGetValue(Id(resolved.OriginalDefinition), out target)) continue;
                    if (target.IsConversion) continue;
                    var occurrence = NormalizeOccurrence(node);
                    if (!target.Seen.Add((tree, occurrence.SpanStart, occurrence.Span.Length, DomainReferenceKind.Usage))) continue;
                    var span = occurrence.GetLocation().GetMappedLineSpan();
                    target.Locations.Add(new DomainReferenceLocation
                    {
                        FilePath = span.Path ?? "",
                        Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1,
                        SpanText = sourceText.GetSubText(occurrence.Span).ToString(),
                        ContainingSymbolId = ComputeContainingSymbolId(model, node),
                        Kind = DomainReferenceKind.Usage,
                    });
                }
            }
        }

        if (options.IncludeDeclarations)
        foreach (var target in targets.Values)
        foreach (var location in target.Symbol.Locations.Where(location => location.IsInSource))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (!target.Seen.Add((location.SourceTree!, location.SourceSpan.Start, location.SourceSpan.Length, DomainReferenceKind.Declaration))) continue;
            var span = location.GetMappedLineSpan();
            target.Locations.Add(new DomainReferenceLocation
            {
                FilePath = span.Path ?? "",
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                SpanText = "(declaration)",
                ContainingSymbolId = target.Id,
                Kind = DomainReferenceKind.Declaration,
            });
        }

        options.CancellationToken.ThrowIfCancellationRequested();
        var locations = targets.ToDictionary(pair => pair.Key, pair => pair.Value.Locations.ToArray(), StringComparer.Ordinal);
        return new ReferenceSearchReport
        {
            Symbols = orderedTargets.Select(row => new ReferenceSearchGroup
            {
                SymbolId = row.target?.Id ?? row.requestedId,
                Resolved = row.target != null,
                Locations = row.target == null ? Array.Empty<DomainReferenceLocation>() : locations[row.target.Id],
            }).ToArray(),
            Work = new ReferenceSearchWork
            {
                SyntaxTreeVisits = treeVisits,
                SyntaxNodeVisits = nodeVisits,
                SyntaxBindingCount = bindings,
                MatchIdBuildCount = idBuilds,
                ConversionScanCount = conversionIds.Length > 0 ? 1 : 0,
            },
        };
    }

    private sealed class Target(string id, ISymbol symbol)
    {
        internal string Id { get; } = id;
        internal ISymbol Symbol { get; } = symbol;
        internal bool IsConversion { get; } = symbol is IMethodSymbol { MethodKind: MethodKind.Conversion };
        internal List<DomainReferenceLocation> Locations { get; } = new();
        // Dedup physical occurrence identity before mapped display coordinates.
        internal HashSet<(SyntaxTree tree, int start, int length, DomainReferenceKind kind)> Seen { get; } = new();
    }

    private static SyntaxNode NormalizeOccurrence(SyntaxNode node)
    {
        // Only the called expression is transparent. An argument or receiver
        // can independently reference the same target and must stay distinct.
        while (node.Parent is { } parent && parent switch
        {
            MemberAccessExpressionSyntax member => member.Name == node,
            MemberBindingExpressionSyntax member => member.Name == node,
            InvocationExpressionSyntax invocation => invocation.Expression == node,
            _ => false,
        })
            node = parent;
        return node;
    }

    /// <summary>
    /// Walk a reference's syntax ancestors to find the first enclosing
    /// member declaration (method, constructor, property, indexer, field,
    /// event) or, as a fallback, its enclosing type declaration. Returns
    /// the canonical Lifeblood symbol ID of that enclosing symbol via
    /// <c>BuildSymbolId</c> so the result compares equal to the same
    /// symbol's entry in the graph. Returns an empty string when no
    /// sensible containing symbol can be found (e.g., a top-level
    /// statement with no enclosing member).
    /// </summary>
    private string ComputeContainingSymbolId(SemanticModel model, SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax m:
                    var ms = model.GetDeclaredSymbol(m);
                    if (ms != null) return _lookup.BuildSymbolId(ms);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax c:
                    var cs = model.GetDeclaredSymbol(c);
                    if (cs != null) return _lookup.BuildSymbolId(cs);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.DestructorDeclarationSyntax d:
                    var ds = model.GetDeclaredSymbol(d);
                    if (ds != null) return _lookup.BuildSymbolId(ds);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.OperatorDeclarationSyntax op:
                    var ops = model.GetDeclaredSymbol(op);
                    if (ops != null) return _lookup.BuildSymbolId(ops);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConversionOperatorDeclarationSyntax co:
                    var cos = model.GetDeclaredSymbol(co);
                    if (cos != null) return _lookup.BuildSymbolId(cos);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax p:
                    var ps = model.GetDeclaredSymbol(p);
                    if (ps != null) return _lookup.BuildSymbolId(ps);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.IndexerDeclarationSyntax idx:
                    var idxs = model.GetDeclaredSymbol(idx);
                    if (idxs != null) return _lookup.BuildSymbolId(idxs);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.EventDeclarationSyntax evd:
                    var evds = model.GetDeclaredSymbol(evd);
                    if (evds != null) return _lookup.BuildSymbolId(evds);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd
                    when vd.Parent?.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax:
                    var fs = model.GetDeclaredSymbol(vd);
                    if (fs != null) return _lookup.BuildSymbolId(fs);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax evfv
                    when evfv.Parent?.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.EventFieldDeclarationSyntax:
                    var efs = model.GetDeclaredSymbol(evfv);
                    if (efs != null) return _lookup.BuildSymbolId(efs);
                    break;
                case Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax t:
                    var ts = model.GetDeclaredSymbol(t);
                    if (ts != null) return _lookup.BuildSymbolId(ts);
                    break;
            }
        }
        return "";
    }
}
