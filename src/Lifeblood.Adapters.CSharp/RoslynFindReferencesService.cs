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

    internal DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        // Direct compilation scan. Reliable across cross-project boundaries
        // where AdhocWorkspace's SymbolFinder.FindReferencesAsync may miss results.
        //
        // Match by CANONICAL Lifeblood symbol ID (BuildSymbolId), NOT by Roslyn's
        // ToDisplayString. The display string can diverge subtly across the source/metadata
        // boundary (nullability, reduced names, attribute round-trips), causing legitimate
        // call sites to be silently dropped. The canonical builder is namespace-walking +
        // explicit param types. It produces the same string for source and metadata symbols
        // because both route through Internal.CanonicalSymbolFormat (GetFullName + the pinned
        // ParamType SymbolDisplayFormat).
        //
        // First we resolve the requested symbol once to get its OWN canonical ID. Then for
        // every visited node we compute the canonical ID and compare strings. This is the
        // same builder the graph uses, so any ID format the graph emits is also matched here.
        var resolvedTarget = _lookup.ResolveFromSource(symbolId);
        if (resolvedTarget == null) return Array.Empty<DomainReferenceLocation>();
        var targetCanonicalId = _lookup.BuildSymbolId(resolvedTarget);

        var results = new List<DomainReferenceLocation>();

        // Physical syntax identity precedes mapped display coordinates: distinct
        // calls can share a line, container, or even a #line mapping.
        var seen = new HashSet<(SyntaxTree tree, int start, int length, DomainReferenceKind kind)>();

        if (resolvedTarget is IMethodSymbol { MethodKind: MethodKind.Conversion })
        {
            // Implicit conversions have no bound method-name syntax. Consume
            // the existing operation authority for both explicit and implicit
            // conversions instead of layering a second syntax scan over it.
            _operationFacts.Scan(new OperationFactQuery
            {
                IncludeKinds = new[] { OperationFactKind.Conversion },
                TargetSymbolIds = new[] { targetCanonicalId },
                IncludeImplicit = true,
                MaxFacts = int.MaxValue,
            }, fact =>
            {
                var owner = CompilationFileOwnershipResolver.Resolve(
                    _lookup.Compilations, fact.Source.FilePath, fact.ModuleName);
                if (owner.Tree is not { } tree)
                    throw new InvalidOperationException("A retained operation fact has no unique source tree.");
                var text = tree.GetText(options.CancellationToken);
                var start = text.Lines[fact.Source.Line - 1].Start + fact.Source.Column - 1;
                var end = text.Lines[fact.Source.EndLine - 1].Start + fact.Source.EndColumn - 1;
                var occurrence = new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start);
                if (!seen.Add((tree, start, occurrence.Length, DomainReferenceKind.Usage))) return true;
                var mapped = tree.GetMappedLineSpan(occurrence, options.CancellationToken);
                results.Add(new DomainReferenceLocation
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
        else foreach (var compilation in _lookup.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(options.CancellationToken);
                var sourceText = tree.GetText(options.CancellationToken);

                foreach (var node in root.DescendantNodes())
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    var symbolInfo = model.GetSymbolInfo(node, options.CancellationToken);
                    var resolved = symbolInfo.Symbol;
                    if (resolved == null) continue;

                    // Compare canonical IDs. Walk to OriginalDefinition for constructed
                    // generics so List<int>.Add and List<string>.Add both match List<T>.Add.
                    var resolvedId = _lookup.BuildSymbolId(resolved);
                    if (resolvedId != targetCanonicalId)
                    {
                        var originalId = _lookup.BuildSymbolId(resolved.OriginalDefinition);
                        if (originalId != targetCanonicalId) continue;
                    }

                    var occurrence = NormalizeOccurrence(node);
                    if (!seen.Add((tree, occurrence.SpanStart, occurrence.Span.Length, DomainReferenceKind.Usage))) continue;
                    var span = occurrence.GetLocation().GetMappedLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    var filePath = span.Path ?? "";

                    // Populate containingSymbolId by walking the node's ancestors to the
                    // first enclosing member declaration (method, property, indexer, field,
                    // ctor) or type declaration as the coarser fallback. The canonical ID
                    // of that member is what find_references consumers use to group usages
                    // by caller, drive containingTypeFilter, and render call-graph UIs.
                    // O(depth) per reference — cheap.
                    var containingSymbolId = ComputeContainingSymbolId(model, node);

                    var spanText = sourceText.GetSubText(occurrence.Span).ToString();
                    results.Add(new DomainReferenceLocation
                    {
                        FilePath = filePath,
                        Line = line,
                        Column = column,
                        SpanText = spanText,
                        ContainingSymbolId = containingSymbolId,
                        Kind = DomainReferenceKind.Usage,
                    });
                }
            }
        }

        // Declaration locations are an opt-in operation policy on the host,
        // NOT a side-effect of resolver
        // merging. Roslyn's ISymbol.Locations returns one entry per partial
        // declaration for partial types. Exactly the data the user wants
        // surfaced when querying "where is this type defined?"
        if (options.IncludeDeclarations)
        {
            var declContainingId = targetCanonicalId; // declaration's own symbol
            if (resolvedTarget != null)
            {
                foreach (var location in resolvedTarget.Locations.Where(l => l.IsInSource))
                {
                    var span = location.GetMappedLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    var filePath = span.Path ?? "";
                    if (!seen.Add((location.SourceTree!, location.SourceSpan.Start, location.SourceSpan.Length, DomainReferenceKind.Declaration))) continue;

                    results.Add(new DomainReferenceLocation
                    {
                        FilePath = filePath,
                        Line = line,
                        Column = column,
                        SpanText = "(declaration)",
                        ContainingSymbolId = declContainingId,
                        Kind = DomainReferenceKind.Declaration,
                    });
                }
            }
        }

        return results.ToArray();
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
