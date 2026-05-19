using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using DomainReferenceLocation = Lifeblood.Domain.Results.ReferenceLocation;
using DomainReferenceKind = Lifeblood.Domain.Results.ReferenceKind;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Extracted find-references service. Direct compilation scan that
/// matches by canonical Lifeblood symbol id (NOT Roslyn's
/// ToDisplayString — the display string can diverge subtly across the
/// source/metadata boundary causing legitimate call sites to be silently
/// dropped). Owns the per-call dedup map, the OriginalDefinition
/// fallback, and the containing-symbol walker.
///
/// Stage 8b — adapter architecture thinning (INV-ADAPTER-THIN-001).
/// Pre-S8b the two FindReferences overloads + ComputeContainingSymbolId
/// lived inline in <see cref="RoslynCompilationHost"/> (~185 LOC of
/// self-contained logic). Behavior preserved across the move; public
/// <see cref="ICompilationHost.FindReferences(string, FindReferencesOptions)"/>
/// contract unchanged.
/// </summary>
internal sealed class RoslynFindReferencesService
{
    private readonly IRoslynLookup _lookup;

    internal RoslynFindReferencesService(IRoslynLookup lookup) => _lookup = lookup;

    internal DomainReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options)
    {
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

        // Logical-reference dedup: an invocation expression `x.Foo(args)` and
        // its identifier token `Foo` are two distinct syntax nodes but ONE
        // logical reference. Key is (filePath, line, containingSymbolId,
        // referencedSymbolId) — since referencedSymbolId is fixed at
        // targetCanonicalId for every hit in this method, the effective key
        // reduces to (filePath, line, containingSymbolId) and one entry per
        // logical call-site is emitted instead of two.
        var seen = new HashSet<(string filePath, int line, string containingSymbolId, string referencedSymbolId)>();

        foreach (var compilation in _lookup.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var sourceText = tree.GetText();

                foreach (var node in root.DescendantNodes())
                {
                    var symbolInfo = model.GetSymbolInfo(node);
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

                    var span = node.GetLocation().GetMappedLineSpan();
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

                    // Dedup key. When containingSymbolId is non-empty, it distinguishes
                    // references by their enclosing member — two unrelated calls on the
                    // same line in different methods stay separate. When containingSymbolId
                    // IS empty (top-level statements, file-scope lambdas, or any node
                    // whose ancestor walk hit no member declaration), fall back to the
                    // node's own start column so distinct call-sites on the same line
                    // still dedup correctly. Without this fallback, `_a.Foo(); _b.Foo();`
                    // at file scope would collapse to one entry.
                    var dedupSlot = string.IsNullOrEmpty(containingSymbolId)
                        ? $":col{column}"
                        : containingSymbolId;
                    if (!seen.Add((filePath, line, dedupSlot, targetCanonicalId))) continue;

                    var spanText = sourceText.GetSubText(node.Span).ToString();
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
            var declSeen = new HashSet<(string, int, string, string)>(seen);
            if (resolvedTarget != null)
            {
                foreach (var location in resolvedTarget.Locations.Where(l => l.IsInSource))
                {
                    var span = location.GetMappedLineSpan();
                    var line = span.StartLinePosition.Line + 1;
                    var column = span.StartLinePosition.Character + 1;
                    var filePath = span.Path ?? "";
                    if (!declSeen.Add((filePath, line, declContainingId, targetCanonicalId))) continue;

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
