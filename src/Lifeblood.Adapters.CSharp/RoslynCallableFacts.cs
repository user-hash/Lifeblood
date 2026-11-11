using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Lifeblood.Adapters.CSharp;

internal sealed partial class RoslynOperationFactProvider
{
    private bool ScanCallableDeclarations(
        SemanticModel model,
        SyntaxNode syntaxRoot,
        string moduleName,
        OperationFactQuery query,
        HashSet<string>? containingFilter,
        HashSet<string>? targetFilter,
        HashSet<string>? kindFilter,
        ScanSelector[]? selectors,
        ScanSymbolIds symbolIds,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken,
        ref int emittedFacts,
        ref bool truncated,
        ref bool stoppedByConsumer)
    {
        const string kind = OperationFactKind.CallableDeclaration;
        if (!Matches(kindFilter, kind)) return true;
        string? checksum = null;
        IReadOnlyDictionary<string, string>? checksums = null;
        foreach (var syntax in syntaxRoot.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (method, hasBody) = DeclaredCallable(model, syntax, cancellationToken);
            if (method == null) continue;
            var targetId = symbolIds.Get(method);
            if (!Matches(targetFilter, targetId)
                || !SelectorCanMatchBeforeContaining(selectors, kind, null, targetId)) continue;
            ISymbol owner = method;
            while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
                owner = nested.ContainingSymbol;
            var ownerId = symbolIds.Get(owner);
            if (!Matches(containingFilter, ownerId)
                || !SelectorMatches(selectors, kind, null, targetId, ownerId)) continue;
            if (emittedFacts >= query.MaxFacts)
            {
                truncated = true;
                return false;
            }

            var text = syntaxRoot.SyntaxTree.GetText(cancellationToken);
            checksum ??= Convert.ToHexString(text.GetChecksum().AsSpan()).ToLowerInvariant();
            checksums ??= new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Sha1"] = TextChecksum(SourceHashAlgorithm.Sha1),
                ["Sha256"] = TextChecksum(SourceHashAlgorithm.Sha256),
            };
            string TextChecksum(SourceHashAlgorithm algorithm)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Convert.ToHexString((text.ChecksumAlgorithm == algorithm ? text
                    : SourceText.From(text.ToString(), text.Encoding, algorithm)).GetChecksum().AsSpan()).ToLowerInvariant();
            }
            var deferred = method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction;
            var fact = new OperationFact
            {
                Id = BuildFactId(moduleName, _profileScope, syntax, kind, 0),
                Kind = kind,
                ModuleName = moduleName,
                ProfileScope = _profileScope,
                ContainingSymbolId = ownerId,
                ContainingCallableId = symbolIds.Callable(method),
                TargetSymbolId = targetId,
                Source = SourceSpan(syntax),
                IsImplicit = false,
                CallableDeclaration = new OperationCallableDeclaration
                {
                    BodyKind = hasBody ? OperationCallableBodyKind.SourceBody : OperationCallableBodyKind.NoSourceBody,
                    HasBindingErrors = model.GetDiagnostics(syntax.Span, cancellationToken)
                        .Any(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
                    IsExternal = method.IsExtern,
                    IsDeferred = deferred,
                    ParentCallableId = deferred && method.ContainingSymbol is IMethodSymbol parent
                        ? symbolIds.Callable(parent) : null,
                    SourceChecksumAlgorithm = text.ChecksumAlgorithm.ToString(),
                    SourceChecksum = checksum,
                    SourceChecksums = checksums,
                    SourceEncoding = text.Encoding?.WebName,
                },
            };
            emittedFacts++;
            if (!consume(fact))
            {
                stoppedByConsumer = true;
                return false;
            }
        }
        return true;
    }

    private static (IMethodSymbol? Method, bool HasBody) DeclaredCallable(
        SemanticModel model,
        SyntaxNode syntax,
        CancellationToken cancellationToken)
        => syntax switch
        {
            BaseMethodDeclarationSyntax method =>
                (model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol,
                    method.Body != null || method.ExpressionBody != null),
            AccessorDeclarationSyntax accessor =>
                (model.GetDeclaredSymbol(accessor, cancellationToken) as IMethodSymbol,
                    accessor.Body != null || accessor.ExpressionBody != null),
            LocalFunctionStatementSyntax local =>
                (model.GetDeclaredSymbol(local, cancellationToken) as IMethodSymbol, local.Body != null || local.ExpressionBody != null),
            AnonymousFunctionExpressionSyntax anonymous =>
                ((model.GetOperation(anonymous, cancellationToken) as IAnonymousFunctionOperation)?.Symbol, true),
            PropertyDeclarationSyntax { ExpressionBody: not null } property =>
                ((model.GetDeclaredSymbol(property, cancellationToken) as IPropertySymbol)?.GetMethod, true),
            IndexerDeclarationSyntax { ExpressionBody: not null } indexer =>
                ((model.GetDeclaredSymbol(indexer, cancellationToken) as IPropertySymbol)?.GetMethod, true),
            _ => default,
        };
}
