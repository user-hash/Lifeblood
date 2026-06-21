using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Shared argument / default-value binding logic for the IOperation-walking
/// extractors (<see cref="RoslynStaticTableExtractor"/> constructor cells and
/// <see cref="RoslynCallsiteArgumentExtractor"/> call sites). Roslyn's
/// <see cref="IArgumentOperation"/> already routes named / positional /
/// optional arguments to their <see cref="IParameterSymbol"/>, so neither
/// consumer re-implements overload resolution — the value here is the single
/// place that re-sources a <see cref="ArgumentKind.DefaultValue"/> argument
/// back to the parameter's own declaration, so the classified value reflects
/// the authoring expression (e.g. <c>Mode.Beta</c>) rather than the lowered
/// constant that <c>arg.Value</c> surfaces at the call site. INV-CALLSITE-ARGS-001.
/// </summary>
internal static class RoslynArgumentBinding
{
    /// <summary>
    /// Resolve the operation to classify for <paramref name="arg"/> plus the
    /// provenance syntax node. For an author-supplied argument this is just
    /// <c>arg.Value</c> / its own syntax. For a default-value argument it is
    /// the parameter's default expression operation (when the parameter has a
    /// source declaration in <paramref name="compilation"/>), so the classified
    /// value and its file/line provenance point at the default expression, not
    /// the call site.
    /// </summary>
    internal static (IOperation ClassifyOp, SyntaxNode? Provenance) Resolve(
        IArgumentOperation arg, CSharpCompilation compilation)
    {
        IOperation classifyOp = arg.Value;
        SyntaxNode? provenance = null;

        if (arg.ArgumentKind == ArgumentKind.DefaultValue && arg.Parameter != null)
        {
            var (paramSyntax, paramOp) = ResolveParameterDefault(arg.Parameter, compilation);
            if (paramSyntax != null)
            {
                provenance = paramSyntax;
                if (paramOp != null) classifyOp = paramOp;
            }
        }

        return (classifyOp, provenance);
    }

    /// <summary>
    /// Resolve the source-syntax node + bound operation for a parameter's
    /// default-value expression. Returns the expression inside <c>= …</c> on
    /// the parameter declaration paired with its <see cref="IOperation"/>, or
    /// <c>(null, null)</c> when the parameter is metadata-only or carries no
    /// source declaration.
    /// </summary>
    internal static (SyntaxNode? Syntax, IOperation? Op) ResolveParameterDefault(
        IParameterSymbol parameter, CSharpCompilation compilation)
    {
        foreach (var reference in parameter.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is ParameterSyntax ps && ps.Default != null)
            {
                var syntax = ps.Default.Value;
                IOperation? op = null;
                if (compilation.SyntaxTrees.Any(t => ReferenceEquals(t, syntax.SyntaxTree)))
                    op = compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
                return (syntax, op);
            }
        }
        return (null, null);
    }
}
