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
        "GetConstructor" => true,  // Type.GetConstructor — can bypass new() blocklist
        "GetConstructors" => true,
        "InvokeMember" => true,
        "Invoke" => true, // MethodInfo.Invoke / ConstructorInfo.Invoke
        "SetValue" => true, // FieldInfo/PropertyInfo.SetValue
        "CreateDelegate" => true,
        "DynamicInvoke" => true,
        "CreateInstance" => true, // Activator.CreateInstance via reflection
        "Compile" => true, // Expression<T>.Compile() — produces unblockable delegates
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

    /// <summary>
    /// Blocked (receiver-type, method-name) pairs. If the terminal method matches
    /// AND any earlier part of the expression chain matches the receiver, it's blocked.
    /// This catches chained calls like Process.GetCurrentProcess().Kill()
    /// where "Kill" is the terminal and "Process" appears earlier in the chain.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> BlockedReceiverMethods = new(StringComparer.Ordinal)
    {
        ["Process"] = new(StringComparer.Ordinal) { "Start", "Kill" },
        ["File"] = new(StringComparer.Ordinal)
        {
            "Delete", "WriteAllText", "WriteAllBytes", "WriteAllLines",
            "AppendAllText", "AppendAllLines", "Create", "Move", "Copy", "SetAttributes",
        },
        ["Directory"] = new(StringComparer.Ordinal) { "Delete", "CreateDirectory", "Move" },
        ["Environment"] = new(StringComparer.Ordinal) { "Exit", "SetEnvironmentVariable" },
        ["Assembly"] = new(StringComparer.Ordinal) { "Load", "LoadFile", "LoadFrom", "UnsafeLoadFrom" },
        ["AppDomain"] = new(StringComparer.Ordinal) { "CreateDomain" },
        ["Thread"] = new(StringComparer.Ordinal) { "Abort" },
        ["Marshal"] = new(StringComparer.Ordinal) { "Copy", "PtrToStructure" },
        ["WebRequest"] = new(StringComparer.Ordinal) { "Create" },
    };

    private static bool IsBlockedStaticCall(MemberAccessExpressionSyntax memberAccess)
    {
        // Reconstruct the full member chain, walking through invocations.
        // Immune to whitespace injection, comment injection, AND chained-call bypass.
        var parts = ReconstructMemberChainParts(memberAccess);
        if (parts.Count < 2) return false;

        var terminalMethod = parts[parts.Count - 1];

        // Structured receiver+method check: if the terminal method is dangerous
        // for a given receiver type, and that receiver appears anywhere earlier
        // in the chain, block it. Catches both direct (Process.Kill) and
        // chained (Process.GetCurrentProcess().Kill) patterns.
        foreach (var (receiver, methods) in BlockedReceiverMethods)
        {
            if (!methods.Contains(terminalMethod)) continue;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                if (parts[i] == receiver) return true;
            }
        }

        // Special case: IL generation (Emit + OpCode co-occurrence in the chain)
        bool hasEmit = false, hasOpCode = false;
        foreach (var part in parts)
        {
            if (part.Contains("Emit")) hasEmit = true;
            if (part.Contains("OpCode")) hasOpCode = true;
        }
        if (hasEmit && hasOpCode) return true;

        return false;
    }

    /// <summary>
    /// Reconstruct a member access chain from AST nodes, stripping all trivia
    /// and walking through invocations. Handles chained calls:
    /// "Process.GetCurrentProcess().Kill()" → ["Process", "GetCurrentProcess", "Kill"]
    /// "System . IO . File . Delete" → ["System", "IO", "File", "Delete"]
    /// </summary>
    private static List<string> ReconstructMemberChainParts(MemberAccessExpressionSyntax memberAccess)
    {
        var parts = new List<string>();
        SyntaxNode current = memberAccess;

        while (true)
        {
            if (current is MemberAccessExpressionSyntax ma)
            {
                parts.Add(ma.Name.Identifier.Text);
                current = ma.Expression;
            }
            else if (current is InvocationExpressionSyntax inv)
            {
                // Walk through invocations to reach the full chain.
                // Process.GetCurrentProcess().Kill → invocation wraps GetCurrentProcess(),
                // we need to walk past it to find Process.
                current = inv.Expression;
            }
            else break;
        }

        if (current is IdentifierNameSyntax id)
            parts.Add(id.Identifier.Text);

        parts.Reverse();
        return parts;
    }
}
