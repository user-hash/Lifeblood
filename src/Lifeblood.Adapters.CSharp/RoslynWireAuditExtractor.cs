using System;
using System.Collections.Generic;
using System.Linq;
using Lifeblood.Adapters.CSharp.Internal;
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

    /// <summary>Compact cap forced by <c>summarize:true</c> — triage shape. INV-LIST-SHAPE-UNIFORM-001.</summary>
    internal const int SummarizeMaxFindings = 25;

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
        public bool IsEvent { get; init; }
        public bool IsDegenerateCallCandidate { get; init; }      // private/internal method, >=1 parameter
        public int Reads;
        public int Writes;   // seeded to 1 when the declaration has an initializer
        public int Subscribes;   // event: += sites
        public int Fires;        // event: raise / delegate-access sites
        public int RealCalls;        // method: call sites with >=1 non-degenerate explicit arg
        public int DegenerateCalls;  // method: call sites with >=1 explicit arg, all degenerate
    }

    internal static WireAuditReport Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        WireAuditOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        // summarize forces the compact cap regardless of caller MaxFindings.
        var maxFindings = (options.Summarize ?? false)
            ? SummarizeMaxFindings
            : (options.MaxFindings is { } m && m > 0 ? m : DefaultMaxFindings);

        // 1. Candidate members from SOURCE types (dedup by canonical id).
        var members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var (moduleName, compilation) in compilations)
        {
            foreach (var type in RoslynOperationFacts.EnumerateTypes(compilation.GlobalNamespace))
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
                    var op = model.GetOperation(node);
                    if (op == null) continue;

                    switch (op)
                    {
                        // (c) subscribe / unsubscribe: obj.Evt += / -= handler.
                        case IEventAssignmentOperation ea
                            when ea.EventReference is IEventReferenceOperation er
                                 && members.TryGetValue(buildSymbolId(er.Event.OriginalDefinition), out var evInfo):
                            if (ea.Adds) evInfo.Subscribes++;
                            continue;
                        // (c) raise / delegate-access: an event reference NOT in a +=/-=
                        // position is a use of the backing delegate (a raise site).
                        case IEventReferenceOperation er2
                            when er2.Parent is not IEventAssignmentOperation
                                 && members.TryGetValue(buildSymbolId(er2.Event.OriginalDefinition), out var evInfo2):
                            evInfo2.Fires++;
                            continue;
                        // (d) classify each call site of a degenerate-call candidate.
                        case IInvocationOperation inv
                            when members.TryGetValue(buildSymbolId(inv.TargetMethod.OriginalDefinition), out var mInfo)
                                 && mInfo.IsDegenerateCallCandidate:
                            ClassifyCallSite(inv, mInfo);
                            continue;
                    }

                    ISymbol? referenced = op switch
                    {
                        IFieldReferenceOperation f => f.Field,
                        IPropertyReferenceOperation p => p.Property,
                        _ => null,
                    };
                    if (referenced == null) continue;
                    var id = buildSymbolId(referenced.OriginalDefinition);
                    if (!members.TryGetValue(id, out var info)) continue;

                    if (RoslynOperationFacts.IsWriteContext(op)) info.Writes++;
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

            if (options.IncludeEvents && info.IsEvent)
            {
                if (info.Subscribes > 0 && info.Fires == 0)
                    findings.Add(MakeFinding(info, WireAuditFindingKind.EventSubscribedNeverRaised,
                        $"event subscribed at {info.Subscribes} site(s) with zero raise sites — handlers attached, nothing fires it"));
                else if (info.Fires > 0 && info.Subscribes == 0)
                    findings.Add(MakeFinding(info, WireAuditFindingKind.EventRaisedNeverSubscribed,
                        $"event raised at {info.Fires} site(s) with zero subscribers — fired into the void"));
            }

            if (options.IncludeDegenerateConstantCallSites && info.IsDegenerateCallCandidate
                && info.DegenerateCalls > 0 && info.RealCalls == 0)
                findings.Add(MakeFinding(info, WireAuditFindingKind.DegenerateConstantCallSites,
                    $"private/internal method whose {info.DegenerateCalls} call site(s) pass only compile-time-degenerate args (constant / default / null); zero call sites pass a runtime value"));
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
            case IEventSymbol e:
            {
                var loc2 = e.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                return new MemberInfo
                {
                    Id = buildSymbolId(e.OriginalDefinition),
                    Name = e.Name,
                    MemberKind = "Event",
                    MemberType = e.Type.ToDisplayString(),
                    DeclaringTypeId = buildSymbolId(type.OriginalDefinition),
                    FilePath = loc2?.Path ?? "",
                    Line = (loc2?.StartLinePosition.Line ?? 0) + 1,
                    ModuleName = moduleName,
                    IsDelegate = false,
                    IsReadWriteCandidate = false,
                    IsEvent = true,
                };
            }
            // Degenerate-call candidate: a private/internal ordinary method with >=1
            // parameter. Public methods are excluded — being called with constants is
            // normal for public API; the smell is a NON-public helper only ever fed
            // constants (a vestigial parameter or placeholder wire).
            case IMethodSymbol mtd when mtd.MethodKind == MethodKind.Ordinary
                && mtd.Parameters.Length > 0 && !mtd.IsAbstract && !mtd.IsExtern
                && (mtd.DeclaredAccessibility == Accessibility.Private
                    || mtd.DeclaredAccessibility == Accessibility.Internal
                    || mtd.DeclaredAccessibility == Accessibility.ProtectedAndInternal):
            {
                var loc3 = mtd.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                return new MemberInfo
                {
                    Id = buildSymbolId(mtd.OriginalDefinition),
                    Name = mtd.Name,
                    MemberKind = "Method",
                    MemberType = mtd.ToDisplayString(),
                    DeclaringTypeId = buildSymbolId(type.OriginalDefinition),
                    FilePath = loc3?.Path ?? "",
                    Line = (loc3?.StartLinePosition.Line ?? 0) + 1,
                    ModuleName = moduleName,
                    IsDelegate = false,
                    IsReadWriteCandidate = false,
                    IsDegenerateCallCandidate = true,
                };
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

    private static bool InScope(MemberInfo info, WireAuditOptions options)
    {
        if (options.TypeId != null && !string.Equals(info.DeclaringTypeId, options.TypeId, StringComparison.Ordinal))
            return false;
        if (options.ModuleScope != null && !string.Equals(info.ModuleName, options.ModuleScope, StringComparison.Ordinal))
            return false;
        return true;
    }

    private static WireAuditFinding MakeFinding(MemberInfo info, string kind, string reason)
    {
        // ReadCount/WriteCount are generic reference tallies whose meaning follows the
        // member: field/property = reads/writes; event = raise/subscribe sites
        // (raising reads the backing delegate, subscribing writes it); method = total
        // call sites / 0.
        var (read, write) = info switch
        {
            { IsEvent: true } => (info.Fires, info.Subscribes),
            { IsDegenerateCallCandidate: true } => (info.DegenerateCalls + info.RealCalls, 0),
            _ => (info.Reads, info.Writes),
        };
        return new WireAuditFinding
        {
            Kind = kind,
            MemberId = info.Id,
            MemberName = info.Name,
            MemberKind = info.MemberKind,
            MemberType = info.MemberType,
            DeclaringTypeId = info.DeclaringTypeId,
            FilePath = info.FilePath,
            Line = info.Line,
            ReadCount = read,
            WriteCount = write,
            Reason = reason,
        };
    }

    /// <summary>
    /// Classify one call site of a degenerate-call candidate: a site whose explicit
    /// args are ALL compile-time-degenerate (constant / <c>default</c> / null)
    /// increments <c>DegenerateCalls</c>; any explicit runtime-valued arg makes it a
    /// <c>RealCalls</c> site. Sites with no explicit args (all defaulted) are ignored
    /// — they say nothing about passing constants. Field-based sentinels like
    /// <c>Vector2.zero</c> are NOT degenerate (they are field references, not
    /// constants); documented as a known gap on the wire.
    /// </summary>
    private static void ClassifyCallSite(IInvocationOperation inv, MemberInfo info)
    {
        var explicitArgs = inv.Arguments.Where(a => a.ArgumentKind != ArgumentKind.DefaultValue).ToArray();
        if (explicitArgs.Length == 0) return;
        if (explicitArgs.All(a => IsDegenerateArg(a.Value))) info.DegenerateCalls++;
        else info.RealCalls++;
    }

    private static bool IsDegenerateArg(IOperation value)
    {
        var v = value;
        while (v is IConversionOperation c && c.Operand != null) v = c.Operand;
        return v is IDefaultValueOperation || v.ConstantValue.HasValue;
    }

    private static WireAuditReport Empty(WireAuditOptions options) => new()
    {
        Scope = options.TypeId ?? options.ModuleScope ?? "(workspace)",
        FindingCount = 0,
        Truncated = false,
        KindBreakdown = new Dictionary<string, int>(StringComparer.Ordinal),
        Findings = Array.Empty<WireAuditFinding>(),
    };
}
