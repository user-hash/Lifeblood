using System;
using System.Collections.Generic;
using System.Linq;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Dead-WIRE extractor. Finds members that are referenced but structurally
/// unplugged: private/internal fields READ with zero writes, and delegate-typed
/// slots never assigned anywhere. One operation-tree pass over every loaded
/// compilation classifies each field/property reference as read or write and
/// accumulates per-member counts; findings fall out of the counts. The candidate
/// set is pre-filtered from SOURCE types so the per-reference dictionary stays
/// small. Operation-tree only — never regex. INV-WIRE-AUDIT-001.
/// </summary>
internal static class RoslynWireAuditExtractor
{
    internal const int DefaultMaxFindings = 200;

    private sealed class MemberInfo
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string MemberKind { get; init; }   // Field | Property
        public required string MemberType { get; init; }
        public required string DeclaringTypeId { get; init; }
        public required string FilePath { get; init; }
        public required int Line { get; init; }
        public required string ModuleName { get; init; }
        public required bool IsDelegate { get; init; }
        public required bool IsReadWriteCandidate { get; init; }  // private/internal field
        public int Reads;
        public int Writes;   // seeded to 1 when the declaration has an initializer
    }

    internal static WireAuditReport Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        WireAuditOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        var maxFindings = options.MaxFindings is { } m && m > 0 ? m : DefaultMaxFindings;

        // 1. Candidate members from SOURCE types (dedup by canonical id).
        var members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var (moduleName, compilation) in compilations)
        {
            foreach (var type in EnumerateTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;
                    var info = TryMakeCandidate(member, type, moduleName, buildSymbolId);
                    if (info != null) members.TryAdd(info.Id, info);
                }
            }
        }
        if (members.Count == 0)
            return Empty(options);

        // 2. One operation-tree pass: classify each reference to a candidate.
        foreach (var (_, compilation) in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    ISymbol? referenced = model.GetOperation(node) switch
                    {
                        IFieldReferenceOperation f => f.Field,
                        IPropertyReferenceOperation p => p.Property,
                        _ => null,
                    };
                    if (referenced == null) continue;
                    var id = buildSymbolId(referenced.OriginalDefinition);
                    if (!members.TryGetValue(id, out var info)) continue;

                    if (IsWriteContext(model.GetOperation(node)!)) info.Writes++;
                    else info.Reads++;
                }
            }
        }

        // 3. Emit findings per pass + scope filter.
        var findings = new List<WireAuditFinding>();
        foreach (var info in members.Values)
        {
            if (!InScope(info, options)) continue;

            if (options.IncludeFieldReadWithoutWrite && info.IsReadWriteCandidate
                && info.Writes == 0 && info.Reads > 0)
                findings.Add(MakeFinding(info, WireAuditFindingKind.FieldReadWithoutWrite,
                    $"private/internal field read at {info.Reads} site(s) with zero write sites (no assignment, no initializer)"));

            if (options.IncludeDelegateSlots && info.IsDelegate && info.Writes == 0)
                findings.Add(MakeFinding(info, WireAuditFindingKind.DelegateSlotNeverAssigned,
                    $"delegate-typed {info.MemberKind.ToLowerInvariant()} with zero assignment sites (read at {info.Reads} site(s))"));
        }

        findings = findings
            .OrderBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

        var kindBreakdown = findings
            .GroupBy(f => f.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var total = findings.Count;
        var clamped = total > maxFindings ? findings.Take(maxFindings).ToArray() : findings.ToArray();

        return new WireAuditReport
        {
            Scope = options.TypeId ?? options.ModuleScope ?? "(workspace)",
            FindingCount = total,
            Truncated = total > clamped.Length,
            KindBreakdown = kindBreakdown,
            Findings = clamped,
        };
    }

    private static MemberInfo? TryMakeCandidate(
        ISymbol member, INamedTypeSymbol type, string moduleName, Func<ISymbol, string> buildSymbolId)
    {
        bool isDelegate;
        bool readWriteCandidate;
        string memberKind;
        ITypeSymbol memberType;
        bool hasInitializer;

        switch (member)
        {
            case IFieldSymbol f when !f.IsConst:
            {
                memberKind = "Field";
                memberType = f.Type;
                isDelegate = f.Type.TypeKind == TypeKind.Delegate && !f.IsReadOnly;
                // Read-without-write only makes sense for mutable, non-readonly,
                // non-public fields (public fields are externally assignable;
                // readonly must be assigned in ctor/initializer or won't compile).
                readWriteCandidate = !f.IsReadOnly
                    && (f.DeclaredAccessibility == Accessibility.Private
                        || f.DeclaredAccessibility == Accessibility.Internal
                        || f.DeclaredAccessibility == Accessibility.ProtectedAndInternal);
                hasInitializer = f.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .Any(v => v.Initializer != null);
                if (!isDelegate && !readWriteCandidate) return null;
                break;
            }
            case IPropertySymbol p when p.SetMethod != null && !p.IsReadOnly:
            {
                memberKind = "Property";
                memberType = p.Type;
                isDelegate = p.Type.TypeKind == TypeKind.Delegate;
                readWriteCandidate = false; // read-without-write pass is field-only (MVP)
                hasInitializer = p.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<PropertyDeclarationSyntax>()
                    .Any(pd => pd.Initializer != null);
                if (!isDelegate) return null;
                break;
            }
            default:
                return null;
        }

        var loc = member.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
        return new MemberInfo
        {
            Id = buildSymbolId(member.OriginalDefinition),
            Name = member.Name,
            MemberKind = memberKind,
            MemberType = memberType.ToDisplayString(),
            DeclaringTypeId = buildSymbolId(type.OriginalDefinition),
            FilePath = loc?.Path ?? "",
            Line = (loc?.StartLinePosition.Line ?? 0) + 1,
            ModuleName = moduleName,
            IsDelegate = isDelegate,
            IsReadWriteCandidate = readWriteCandidate,
            Writes = hasInitializer ? 1 : 0,
        };
    }

    /// <summary>
    /// True if the reference operation sits in a write position: assignment
    /// target (simple / compound / coalesce), increment/decrement target, or a
    /// ref/out argument. Everything else is a read. Compound assignment counts
    /// as a write (the member IS assigned) even though it also reads.
    /// </summary>
    private static bool IsWriteContext(IOperation reference)
    {
        switch (reference.Parent)
        {
            case IAssignmentOperation a:
                return ReferenceEquals(a.Target, reference);
            case IIncrementOrDecrementOperation inc:
                return ReferenceEquals(inc.Target, reference);
            case IArgumentOperation arg:
                return arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out;
            default:
                return false;
        }
    }

    private static bool InScope(MemberInfo info, WireAuditOptions options)
    {
        if (options.TypeId != null && !string.Equals(info.DeclaringTypeId, options.TypeId, StringComparison.Ordinal))
            return false;
        if (options.ModuleScope != null && !string.Equals(info.ModuleName, options.ModuleScope, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static WireAuditFinding MakeFinding(MemberInfo info, string kind, string reason) => new()
    {
        Kind = kind,
        MemberId = info.Id,
        MemberName = info.Name,
        MemberKind = info.MemberKind,
        MemberType = info.MemberType,
        DeclaringTypeId = info.DeclaringTypeId,
        FilePath = info.FilePath,
        Line = info.Line,
        ReadCount = info.Reads,
        WriteCount = info.Writes,
        Reason = reason,
    };

    private static WireAuditReport Empty(WireAuditOptions options) => new()
    {
        Scope = options.TypeId ?? options.ModuleScope ?? "(workspace)",
        FindingCount = 0,
        Truncated = false,
        KindBreakdown = new Dictionary<string, int>(StringComparer.Ordinal),
        Findings = Array.Empty<WireAuditFinding>(),
    };

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in EnumerateTypes(child)) yield return t;
            else if (member is INamedTypeSymbol type)
                foreach (var t in EnumerateWithNested(type)) yield return t;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateWithNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var t in EnumerateWithNested(nested)) yield return t;
    }
}
