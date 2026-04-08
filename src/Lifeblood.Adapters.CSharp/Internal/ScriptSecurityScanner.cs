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

                // Block 'dynamic' keyword — bypasses compile-time checks.
                // Roslyn parses `dynamic` as IdentifierNameSyntax (contextual keyword).
                // Don't check parent type — `dynamic x = ...;` has parent VariableDeclarationSyntax
                // (not TypeSyntax), so the old parent check missed the most common pattern.
                case IdentifierNameSyntax id when id.Identifier.Text == "dynamic":
                    return "Blocked: 'dynamic' keyword — bypasses type safety";

                // Block dangerous object creation — immune to comment/whitespace injection.
                // String blocklist catches "new FileInfo" but comments bypass it.
                case ObjectCreationExpressionSyntax creation:
                {
                    var typeName = creation.Type.ToString().Replace(" ", "");
                    if (IsBlockedCreationType(typeName))
                        return $"Blocked: 'new {typeName}' — restricted type";
                    break;
                }

                // Block target-typed new: "Process p = new();" bypasses ObjectCreationExpressionSyntax.
                // Walk up to the variable declaration to find the declared type.
                case ImplicitObjectCreationExpressionSyntax implicitNew:
                {
                    if (implicitNew.Parent is EqualsValueClauseSyntax
                        && implicitNew.Parent.Parent is VariableDeclaratorSyntax
                        && implicitNew.Parent.Parent.Parent is VariableDeclarationSyntax varDecl)
                    {
                        var typeName = varDecl.Type.ToString().Replace(" ", "");
                        if (IsBlockedCreationType(typeName))
                            return $"Blocked: 'new {typeName}()' — restricted type";
                    }
                    break;
                }

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

    /// <summary>
    /// Blocked types for object creation. Mirrors "new X" patterns from string blocklist
    /// but immune to comment/whitespace injection in the type name.
    /// </summary>
    private static bool IsBlockedCreationType(string typeName) =>
        typeName.EndsWith("FileInfo") || typeName.EndsWith("DirectoryInfo")
        || typeName.EndsWith("StreamWriter") || typeName.EndsWith("FileStream")
        || typeName.EndsWith("HttpClient") || typeName.EndsWith("TcpClient")
        || typeName.EndsWith("Socket")
        || typeName == "Process" || typeName.EndsWith(".Process") // blocks `new Process()` — prevents `p.Start()` bypass
        || typeName.EndsWith("ProcessStartInfo");

    private static bool IsBlockedStaticCall(MemberAccessExpressionSyntax memberAccess)
    {
        // Reconstruct the member chain from AST nodes — immune to whitespace/comment injection.
        // "Process . Start" and "Process/**/. Start" both produce "Process.Start".
        var fullText = ReconstructMemberChain(memberAccess);

        // Process-related
        if (fullText.Contains("Process.Start") || fullText.Contains("Process.Kill"))
            return true;

        // File system mutations — mirrors string blocklist but immune to comment/whitespace bypass
        if (fullText.Contains("File.Delete") || fullText.Contains("File.WriteAll")
            || fullText.Contains("File.AppendAll") || fullText.Contains("File.Create")
            || fullText.Contains("File.Move") || fullText.Contains("File.Copy")
            || fullText.Contains("File.SetAttributes"))
            return true;

        if (fullText.Contains("Directory.Delete") || fullText.Contains("Directory.CreateDirectory")
            || fullText.Contains("Directory.Move"))
            return true;

        // Environment
        if (fullText.Contains("Environment.Exit") || fullText.Contains("Environment.SetEnvironmentVariable"))
            return true;

        // Assembly loading (all Load* variants)
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

        // P/Invoke / Marshal
        if (fullText.Contains("Marshal.Copy") || fullText.Contains("Marshal.PtrToStructure"))
            return true;

        // Network
        if (fullText.Contains("WebRequest.Create"))
            return true;

        return false;
    }

    /// <summary>
    /// Reconstruct a member access chain from AST nodes, stripping all trivia.
    /// "Process . Start" → "Process.Start", "System . IO . File" → "System.IO.File".
    /// </summary>
    private static string ReconstructMemberChain(MemberAccessExpressionSyntax memberAccess)
    {
        var parts = new List<string>();
        SyntaxNode current = memberAccess;

        while (current is MemberAccessExpressionSyntax ma)
        {
            parts.Add(ma.Name.Identifier.Text);
            current = ma.Expression;
        }

        if (current is IdentifierNameSyntax id)
            parts.Add(id.Identifier.Text);

        parts.Reverse();
        return string.Join(".", parts);
    }
}
