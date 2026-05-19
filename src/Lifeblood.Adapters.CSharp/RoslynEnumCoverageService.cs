using Lifeblood.Adapters.CSharp.Internal;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Extracted enum-coverage service. Walks every loaded compilation
/// once, classifies each enum-member reference by parent syntax, and
/// returns an <see cref="EnumCoverageReport"/>. Owns the per-member
/// counter map + the reference-site classifier.
///
/// Stage 8a — adapter architecture thinning (INV-ADAPTER-THIN-001).
/// Pre-S8a the GetEnumCoverage method + its classifier + counter
/// struct lived inline in <see cref="RoslynCompilationHost"/> (~115
/// LOC of self-contained logic). The host orchestrates compilation,
/// resolution, and per-tool delegation; the enum walker is a
/// specialised sibling that needs Compilations + ResolveFromSource +
/// BuildSymbolId, nothing else. Extracting it shrinks the host
/// toward the "trend below 600 LOC" Stage 8 acceptance criterion
/// without changing tool behavior.
///
/// Public contract (<see cref="Application.Ports.Left.ICompilationHost.GetEnumCoverage"/>)
/// + wire shape are unchanged — pinned by 16 EnumCoverageTests fixtures
/// (8 INV-ENUM-COVERAGE-001 + 8 INV-ENUM-COVERAGE-DISPATCH-TABLE-001).
/// </summary>
internal sealed class RoslynEnumCoverageService
{
    private readonly IRoslynLookup _lookup;

    internal RoslynEnumCoverageService(IRoslynLookup lookup) => _lookup = lookup;

    internal EnumCoverageReport? GetEnumCoverage(string enumTypeId)
    {
        if (string.IsNullOrEmpty(enumTypeId)) return null;
        var resolved = _lookup.ResolveFromSource(enumTypeId);
        if (resolved is not INamedTypeSymbol enumSymbol) return null;
        if (enumSymbol.TypeKind != TypeKind.Enum) return null;

        // Build the per-member counter map keyed by canonical Lifeblood id.
        // Enum members are surfaced by Roslyn as const IFieldSymbol on the
        // enum type. Declaration order matters for caller display so we
        // preserve it via a parallel list.
        var orderedMembers = new List<(string Id, string Name)>();
        var counters = new Dictionary<string, EnumMemberCounter>(StringComparer.Ordinal);
        foreach (var member in enumSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.IsConst) continue;
            var id = _lookup.BuildSymbolId(member);
            if (counters.ContainsKey(id)) continue;
            counters[id] = new EnumMemberCounter();
            orderedMembers.Add((id, member.Name));
        }
        if (orderedMembers.Count == 0)
            return new EnumCoverageReport
            {
                EnumTypeId = enumTypeId,
                EnumTypeName = enumSymbol.Name,
                Members = Array.Empty<EnumMemberCoverage>(),
                UnproducedCount = 0,
                UnreferencedCount = 0,
            };

        // Single O(total_nodes) pass per compilation. Walk every descendant
        // node, ask the semantic model which symbol it resolves to, and if
        // the canonical id matches one of our enum members, classify by
        // parent syntax. Cheaper than calling FindReferences per-member
        // (N members × full-tree walk each) on big enums.
        foreach (var compilation in _lookup.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    // Skip the inner Name part of any qualified reference — `Mode.A`
                    // visits as the outer node AND its inner `A` identifier, both of
                    // which `GetSymbolInfo` binds to the same enum-member field.
                    // Without the guard each qualified reference double-counts.
                    // The outer visit carries the full enclosing expression context
                    // the classifier needs anyway, so processing at that level is
                    // strictly more complete. Two outer shapes need covering:
                    // `MemberAccessExpressionSyntax` (`Mode.A` in expression
                    // position) and `QualifiedNameSyntax` (`Mode.A` in type-syntax
                    // position — the legacy parse of the right side of `is`). Bare
                    // identifiers (e.g. `using static Acme.Mode;` then `A`) have no
                    // qualified parent and still pass through this guard.
                    if (node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax parentMae
                        && ReferenceEquals(parentMae.Name, node)) continue;
                    if (node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax parentQn
                        && ReferenceEquals(parentQn.Right, node)) continue;

                    var sym = model.GetSymbolInfo(node).Symbol;
                    if (sym is null || sym.Kind != Microsoft.CodeAnalysis.SymbolKind.Field) continue;

                    var canonical = _lookup.BuildSymbolId(sym);
                    if (!counters.TryGetValue(canonical, out var counter))
                    {
                        var originalDefId = _lookup.BuildSymbolId(sym.OriginalDefinition);
                        if (!counters.TryGetValue(originalDefId, out counter)) continue;
                    }

                    counter.Total++;
                    switch (ClassifyEnumReferenceSite(node))
                    {
                        case EnumRefClass.Produced: counter.Produced++; break;
                        case EnumRefClass.Comparison: counter.Comparison++; break;
                        case EnumRefClass.SwitchPattern: counter.SwitchPattern++; break;
                        case EnumRefClass.Other: break;
                    }
                    // Additive: a dispatch-table cell still counts under whichever
                    // EnumRefClass bucket its syntactic position falls in (typically
                    // Produced), AND increments this counter so the
                    // "value is only a routing key" triage signal stays surface-able.
                    // Reuses the static_tables tool's recognition classifier — no
                    // text grep, single SSoT. INV-ENUM-COVERAGE-DISPATCH-TABLE-001.
                    if (RoslynStaticTableExtractor.IsInsideStaticTableInitializer(model, node))
                        counter.DispatchTable++;
                }
            }
        }

        var rows = new EnumMemberCoverage[orderedMembers.Count];
        int unproduced = 0, unreferenced = 0;
        for (int i = 0; i < orderedMembers.Count; i++)
        {
            var (id, name) = orderedMembers[i];
            var c = counters[id];
            bool isUnref = c.Total == 0;
            bool isUnprod = c.Produced == 0 && c.Total > 0;
            if (isUnref) unreferenced++;
            if (isUnprod) unproduced++;
            rows[i] = new EnumMemberCoverage
            {
                MemberId = id,
                Name = name,
                TotalReferences = c.Total,
                ProducedCount = c.Produced,
                ConsumedComparisonCount = c.Comparison,
                ConsumedSwitchCount = c.SwitchPattern,
                DispatchTableReferenceCount = c.DispatchTable,
                IsUnproduced = isUnprod,
                IsUnreferenced = isUnref,
            };
        }

        return new EnumCoverageReport
        {
            EnumTypeId = enumTypeId,
            EnumTypeName = enumSymbol.Name,
            Members = rows,
            UnproducedCount = unproduced,
            UnreferencedCount = unreferenced,
        };
    }

    /// <summary>
    /// Reference-site role for an enum member's use, derived from its
    /// parent syntax. <see cref="EnumRefClass.Other"/> is the
    /// not-classified bucket — counted in TotalReferences only.
    /// </summary>
    private enum EnumRefClass { Other, Produced, Comparison, SwitchPattern }

    private sealed class EnumMemberCounter
    {
        public int Total;
        public int Produced;
        public int Comparison;
        public int SwitchPattern;
        public int DispatchTable;
    }

    /// <summary>
    /// Classify one enum-member reference site. <paramref name="node"/>
    /// is the syntax node Roslyn resolved to the enum-member field —
    /// typically an <c>IdentifierNameSyntax</c> nested inside a
    /// <c>MemberAccessExpressionSyntax</c> (<c>FieldMask.ShimmerPhase</c>).
    /// We start the parent walk from the enclosing member-access so the
    /// classifier sees the EXPRESSION that uses the value, not the bare
    /// identifier token. Walks up the parent chain until we hit a
    /// classifying node or leave expression context.
    /// </summary>
    private static EnumRefClass ClassifyEnumReferenceSite(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        Microsoft.CodeAnalysis.SyntaxNode current = node;
        // Surface the enclosing MemberAccessExpression so the classifier
        // matches the EXPRESSION, not just the member's IdentifierNameSyntax.
        if (current.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax mae && mae.Name == current)
            current = mae;

        var parent = current.Parent;
        while (parent != null)
        {
            switch (parent)
            {
                // Production sites — value flows into a receiver.
                case Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax assign
                    when ReferenceEquals(assign.Right, current):
                    return EnumRefClass.Produced;
                case Microsoft.CodeAnalysis.CSharp.Syntax.EqualsValueClauseSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.YieldStatementSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ArrowExpressionClauseSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax:
                    return EnumRefClass.Produced;

                // Comparison consumption (==, !=, <, <=, >, >=).
                case Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax bin
                    when bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EqualsExpression)
                      || bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NotEqualsExpression)
                      || bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LessThanExpression)
                      || bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LessThanOrEqualExpression)
                      || bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GreaterThanExpression)
                      || bin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GreaterThanOrEqualExpression):
                    return EnumRefClass.Comparison;

                // Pattern / switch consumption. `m is Mode.A` is treated as
                // pattern-style matching even when Roslyn parses it as the
                // legacy `BinaryExpressionSyntax(IsExpression)` (the parser
                // picks this shape whenever the RHS is a qualified name —
                // semantic binding still resolves the enum-member field, but
                // the syntax stays in the legacy form). Classify it alongside
                // `IsPatternExpressionSyntax` so callers get the same bucket
                // regardless of which syntax shape the parser happened to pick.
                case Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax isBin
                    when isBin.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IsExpression):
                    return EnumRefClass.SwitchPattern;
                case Microsoft.CodeAnalysis.CSharp.Syntax.IsPatternExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConstantPatternSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.CaseSwitchLabelSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.CasePatternSwitchLabelSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.SwitchExpressionArmSyntax:
                    return EnumRefClass.SwitchPattern;
            }

            // Stop at boundaries that aren't a value-receiving expression: a
            // statement / member declaration means we've left the expression
            // tree without hitting a classifier — call it Other.
            if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax) return EnumRefClass.Other;
            if (parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax) return EnumRefClass.Other;

            current = parent;
            parent = parent.Parent;
        }
        return EnumRefClass.Other;
    }
}
