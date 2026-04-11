using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// Decides whether a user-supplied <c>compile_check</c> snippet should be
/// passed to the compiler as-is or wrapped in a synthetic method body so it
/// compiles inside library modules.
///
/// The bug class this resolves: <c>lifeblood_compile_check</c> takes a
/// "snippet" per its public contract, but the previous implementation fed
/// the raw text to <c>CSharpSyntaxTree.ParseText</c> and pasted the result
/// straight into the target module compilation. Library modules
/// (<c>OutputType=Library</c>, the overwhelming majority of csprojs in any
/// real workspace) refuse top-level statements with <c>CS8805 Program
/// using top-level statements must be an executable</c>. The user's
/// `var x = 1 + 1;` failed loudly even though semantically the code is
/// fine — they have to manually wrap with <c>class _Probe { void M() { ... } }</c>
/// to test anything that isn't already a complete CompilationUnit.
///
/// Architectural decision: snippet shape is detectable from the parsed
/// syntax tree. If the parsed CompilationUnit declares any type, namespace,
/// or delegate at the top level, the user wrote (or partially wrote) a
/// complete unit and we pass through. Otherwise the snippet is statements
/// or expressions, and we wrap them in a synthetic class+method.
///
/// Line remapping: when wrapping inserts a single synthetic line above
/// the user's body, every body diagnostic shifts down by one. The wrapper
/// reports the synthetic <see cref="WrapperLineNumber"/> so the caller can
/// subtract it to recover the user's original line. Lines that sit
/// strictly above the insertion (preserved using directives) are not
/// shifted and need no remapping.
///
/// Limitation: only one wrapper line is inserted, so the line math is
/// "subtract one if the diagnostic line is past the wrapper opening,
/// leave alone otherwise". Multi-line wrapper variants would need the
/// caller to apply a per-region offset.
///
/// Pinned by <c>SnippetWrapperTests</c> in the test project.
/// </summary>
internal static class SnippetWrapper
{
    /// <summary>
    /// Stable identifier for the synthetic wrapper class. Chosen to be
    /// unique enough that real user code is unlikely to collide with it,
    /// while still being readable in a stack trace.
    /// </summary>
    public const string WrapperTypeName = "_LifebloodCompileProbe";

    /// <summary>
    /// Stable identifier for the synthetic wrapper method.
    /// </summary>
    public const string WrapperMethodName = "_LifebloodCompileBody";

    /// <summary>
    /// Result of preparing a snippet for compilation.
    /// </summary>
    /// <param name="Tree">
    /// The syntax tree to feed to the compiler. May be the original tree
    /// (if the snippet was already a full CompilationUnit) or a wrapped
    /// tree built around the snippet's statements.
    /// </param>
    /// <param name="WasWrapped">
    /// True if the original snippet was wrapped in a synthetic class+method.
    /// </param>
    /// <param name="WrapperLineNumber">
    /// 1-based line number of the wrapper opening line in the wrapped
    /// source, or 0 if the snippet was not wrapped. Diagnostics whose
    /// raw line is strictly greater than this number must subtract 1 to
    /// recover the user's original line.
    /// </param>
    public readonly record struct PrepareResult(SyntaxTree Tree, bool WasWrapped, int WrapperLineNumber);

    /// <summary>
    /// Parse the snippet, decide whether to wrap, and return the resulting
    /// syntax tree. The decision is purely structural: if the parsed
    /// CompilationUnit contains any type / namespace / delegate
    /// declaration, pass through; otherwise wrap.
    /// </summary>
    public static PrepareResult Prepare(string code)
    {
        if (code == null) throw new ArgumentNullException(nameof(code));

        var originalTree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)originalTree.GetRoot();

        if (HasTopLevelTypeOrNamespace(root))
        {
            return new PrepareResult(originalTree, WasWrapped: false, WrapperLineNumber: 0);
        }

        return WrapAsMethodBody(originalTree, root, code);
    }

    /// <summary>
    /// Remap a diagnostic line from wrapped-source coordinates back to the
    /// user's original snippet coordinates. Safe to call when
    /// <paramref name="result"/> is unwrapped — returns the input line
    /// unchanged.
    /// </summary>
    public static int MapLineToUser(in PrepareResult result, int rawLineOneBased)
    {
        if (!result.WasWrapped || result.WrapperLineNumber == 0) return rawLineOneBased;
        if (rawLineOneBased <= result.WrapperLineNumber) return rawLineOneBased;
        // Anything strictly past the wrapper opening shifts up by one
        // because the synthetic opening sits between the (possibly empty)
        // using prefix and the user body.
        return rawLineOneBased - 1;
    }

    private static bool HasTopLevelTypeOrNamespace(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            if (member is BaseTypeDeclarationSyntax) return true;
            if (member is NamespaceDeclarationSyntax) return true;
            if (member is FileScopedNamespaceDeclarationSyntax) return true;
            if (member is DelegateDeclarationSyntax) return true;
        }
        return false;
    }

    private static PrepareResult WrapAsMethodBody(SyntaxTree originalTree, CompilationUnitSyntax root, string originalCode)
    {
        // Insertion point: immediately after the last using directive's
        // FullSpan end (which includes its trailing trivia, normally a
        // newline). If there are no usings, insert at offset 0.
        int wrapperInsertionOffset = root.Usings.Count > 0
            ? root.Usings[root.Usings.Count - 1].FullSpan.End
            : 0;

        var sourceText = originalTree.GetText();
        var prefix = wrapperInsertionOffset > 0
            ? sourceText.GetSubText(new TextSpan(0, wrapperInsertionOffset)).ToString()
            : string.Empty;
        var body = wrapperInsertionOffset > 0
            ? sourceText.GetSubText(wrapperInsertionOffset).ToString()
            : originalCode;

        // Make sure the prefix ends with a newline so the wrapper sits on
        // its own line. Real csproj formatting nearly always ends a using
        // line in \r\n or \n, but a single-line "using System; var x;"
        // input where the user omitted the newline would otherwise put
        // the wrapper on the same physical line and break the line math.
        if (prefix.Length > 0 && !EndsWithNewline(prefix))
        {
            prefix += "\n";
        }

        // Make sure the body ends with a newline so the wrapper closing
        // sits on its own line and any final diagnostic line is well
        // defined.
        var trailing = body.Length == 0 || EndsWithNewline(body) ? string.Empty : "\n";

        // Wrapper opening on its own physical line. The body content
        // starts on the next physical line, so the user's original line N
        // becomes wrapped line (N + 1) after the insertion point. The
        // closing braces sit on yet another line; their position is
        // irrelevant to remapping because they trail the user's last line
        // and the user never types diagnostics there.
        var wrapperOpening = $"class {WrapperTypeName} {{ void {WrapperMethodName}() {{\n";
        var wrapperClosing = "} }\n";

        var wrapped = prefix + wrapperOpening + body + trailing + wrapperClosing;

        // Wrapper line number in 1-based output coordinates: count newlines
        // in the prefix + 1 (the wrapper opening sits on the line right
        // after the prefix).
        var prefixLineCount = CountLines(prefix);
        var wrapperLineNumber = prefixLineCount + 1;

        var wrappedTree = CSharpSyntaxTree.ParseText(wrapped);
        return new PrepareResult(wrappedTree, WasWrapped: true, WrapperLineNumber: wrapperLineNumber);
    }

    private static bool EndsWithNewline(string s)
        => s.Length > 0 && (s[s.Length - 1] == '\n' || s[s.Length - 1] == '\r');

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n') count++;
            else if (s[i] == '\r')
            {
                count++;
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
        }
        return count;
    }
}
