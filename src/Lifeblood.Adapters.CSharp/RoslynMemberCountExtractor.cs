using System;
using System.Linq;
using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Declared-member counter for one type. <c>reflectionDeclared</c> reproduces
/// <c>System.Reflection</c> DeclaredOnly counting bit-exactly (methods incl.
/// accessors/operators + constructors + fields + properties + events, with
/// <c>[CompilerGenerated]</c> / backing-field members filtered and nested types
/// excluded — the implicit default ctor counts, it is not compiler-generated);
/// <c>sourceSymbols</c> counts source-declared member symbols (no synthesized
/// accessors/backing fields, no implicit ctor) and INCLUDES nested types, matching
/// the Lifeblood graph's child-symbol semantics. Parity with real reflection is
/// pinned by an emit-reflect-vs-parse harness. INV-MEMBER-COUNT-001.
/// </summary>
internal static class RoslynMemberCountExtractor
{
    private const string CompilerGenerated = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    internal static MemberCountReport ReflectionDeclared(string canonicalTypeId, INamedTypeSymbol type)
    {
        int methods = 0, ctors = 0, fields = 0, props = 0, events = 0, nested = 0, excluded = 0;
        foreach (var m in type.GetMembers())
        {
            if (m is INamedTypeSymbol) { nested++; continue; }
            if (IsReflectionCompilerGenerated(m)) { excluded++; continue; }
            switch (m)
            {
                case IMethodSymbol method:
                    if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) ctors++;
                    else methods++;
                    break;
                case IFieldSymbol: fields++; break;
                case IPropertySymbol: props++; break;
                case IEventSymbol: events++; break;
            }
        }

        return Report(canonicalTypeId, MemberCountSemantics.ReflectionDeclared,
            methods + ctors + fields + props + events,
            methods, ctors, fields, props, events, nested, excluded);
    }

    internal static MemberCountReport SourceSymbols(string canonicalTypeId, INamedTypeSymbol type)
    {
        int methods = 0, ctors = 0, fields = 0, props = 0, events = 0, nested = 0, excluded = 0;
        foreach (var m in type.GetMembers())
        {
            // Synthesized (backing fields, implicit ctor) and property/event
            // accessors are not independent source declarations — the graph does
            // not emit a child symbol for them.
            if (m.IsImplicitlyDeclared
                || (m is IMethodSymbol acc && acc.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                    or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise))
            {
                excluded++;
                continue;
            }
            switch (m)
            {
                case INamedTypeSymbol: nested++; break;
                case IMethodSymbol method:
                    if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor) ctors++;
                    else methods++;
                    break;
                case IFieldSymbol: fields++; break;
                case IPropertySymbol: props++; break;
                case IEventSymbol: events++; break;
            }
        }

        return Report(canonicalTypeId, MemberCountSemantics.SourceSymbols,
            methods + ctors + fields + props + events + nested,
            methods, ctors, fields, props, events, nested, excluded);
    }

    /// <summary>
    /// True for members reflection's <c>[CompilerGenerated]</c> filter drops:
    /// implicitly-declared backing FIELDS (auto-property / field-like event),
    /// implicitly-declared property/event ACCESSORS (reflection marks synthesized
    /// <c>get_/set_/add_/remove_</c> compiler-generated — but user-written accessors
    /// are NOT implicitly declared, so they still count), or anything carrying the
    /// <c>[CompilerGenerated]</c> attribute. The implicit default CONSTRUCTOR is
    /// implicitly declared but is neither a field nor an accessor, so it counts —
    /// matching reflection, which does not mark it compiler-generated.
    /// </summary>
    private static bool IsReflectionCompilerGenerated(ISymbol m)
    {
        if (m is IFieldSymbol && m.IsImplicitlyDeclared) return true;         // backing fields
        if (m is IMethodSymbol method && IsSynthesizedAccessor(method)) return true;
        return m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == CompilerGenerated);
    }

    /// <summary>
    /// A property/event accessor the compiler synthesizes a body for — reflection
    /// marks these <c>[CompilerGenerated]</c>. Two shapes: a field-like event
    /// accessor (implicitly declared, no syntax) and an auto-property accessor
    /// (<c>get;</c> / <c>set;</c> / <c>init;</c> — present in syntax but with NO
    /// block body and NO expression body). User-written accessors (<c>get => …;</c>,
    /// <c>add { … }</c>) have a body and are not synthesized, so they still count.
    /// </summary>
    private static bool IsSynthesizedAccessor(IMethodSymbol m)
    {
        if (m.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise))
            return false;
        if (m.IsImplicitlyDeclared) return true;
        return m.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<AccessorDeclarationSyntax>()
            .Any(a => a.Body == null && a.ExpressionBody == null);
    }

    private static MemberCountReport Report(
        string typeId, string semantics, int count,
        int methods, int ctors, int fields, int props, int events, int nested, int excluded)
        => new()
        {
            TypeId = typeId,
            Semantics = semantics,
            Count = count,
            Breakdown = new MemberCountBreakdown
            {
                Methods = methods,
                Constructors = ctors,
                Fields = fields,
                Properties = props,
                Events = events,
                NestedTypes = nested,
                Excluded = excluded,
            },
        };
}
