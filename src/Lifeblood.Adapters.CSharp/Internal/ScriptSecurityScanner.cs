using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// AST-based security scanner for user-submitted code.
/// Catches patterns that string-based blocklists miss (reflection, dynamic invocation).
/// Defense-in-depth — runs before execution, not instead of the blocklist.
/// </summary>
internal static class ScriptSecurityScanner
{
    /// <summary>
    /// Scans a code string for blocked API usage patterns.
    /// Returns null if clean, or a description of what was blocked.
    /// Uses Roslyn syntax analysis — no semantic model needed (fast, no compilation).
    /// </summary>
    public static string? Scan(string code)
    {
        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(code);
        }
        catch
        {
            // Unparseable code must NOT pass security — could be obfuscation attempt.
            // The script engine will also reject it, but we block early as defense-in-depth.
            return "Blocked: code failed to parse — possible obfuscation attempt";
        }

        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // Block typeof(...).GetMethod / .GetMethods / .InvokeMember — reflection bypass
                case MemberAccessExpressionSyntax memberAccess:
                {
                    var name = memberAccess.Name.Identifier.Text;
                    if (IsBlockedReflectionMethod(name))
                        return $"Blocked: reflection API '{name}' — use direct calls instead";

                    // Block Assembly.Load*, AppDomain.CreateDomain
                    if (IsBlockedStaticCall(memberAccess))
                        return $"Blocked: '{memberAccess}' — restricted API";
                    break;
                }

                // Block 'dynamic' keyword — bypasses compile-time checks
                case IdentifierNameSyntax id when id.Identifier.Text == "dynamic"
                    && id.Parent is TypeSyntax:
                    return "Blocked: 'dynamic' keyword — bypasses type safety";

                // Block unsafe keyword
                case UnsafeStatementSyntax:
                    return "Blocked: 'unsafe' block — restricted in sandboxed execution";

                // Block pointer types
                case PointerTypeSyntax:
                    return "Blocked: pointer types — restricted in sandboxed execution";
            }
        }

        return null;
    }

    private static bool IsBlockedReflectionMethod(string name) => name switch
    {
        "GetMethod" => true,
        "GetMethods" => true,
        "GetField" => true,
        "GetFields" => true,
        "GetProperty" => true,
        // GetProperties intentionally allowed — commonly used for read-only inspection
        "InvokeMember" => true,
        "Invoke" => true, // MethodInfo.Invoke
        "SetValue" => true, // FieldInfo/PropertyInfo.SetValue
        "CreateDelegate" => true,
        "DynamicInvoke" => true,
        "CreateInstance" => true, // Activator.CreateInstance via reflection
        _ => false,
    };

    private static bool IsBlockedStaticCall(MemberAccessExpressionSyntax memberAccess)
    {
        var fullText = memberAccess.ToString();

        // Process-related
        if (fullText.Contains("Process.Start") || fullText.Contains("Process.Kill"))
            return true;

        // Assembly loading
        if (fullText.Contains("Assembly.Load") || fullText.Contains("Assembly.UnsafeLoad"))
            return true;

        // AppDomain
        if (fullText.Contains("AppDomain.CreateDomain"))
            return true;

        // IL generation
        if (fullText.Contains("Emit") && fullText.Contains("OpCode"))
            return true;

        // Thread abort
        if (fullText.Contains("Thread.Abort"))
            return true;

        return false;
    }
}
