using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// Ratchets the Unity outer-adapter contract against the discovery and polling
/// conventions owned by MCP for Unity. These files compile in Unity rather than
/// the Lifeblood solution, so syntax-level tests keep schema publication from
/// silently regressing to empty argument objects.
/// </summary>
public sealed class UnityBridgeContractTests
{
    [Fact]
    public void ToolParameters_AreInstancePropertiesOnNestedParametersTypes()
    {
        var root = Parse(ToolsPath);
        var parameterMembers = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(HasToolParameterAttribute)
            .ToArray();

        Assert.NotEmpty(parameterMembers);
        foreach (var member in parameterMembers)
        {
            var property = Assert.IsType<PropertyDeclarationSyntax>(member);
            var owner = Assert.IsType<ClassDeclarationSyntax>(property.Parent);
            Assert.Equal("Parameters", owner.Identifier.ValueText);
            Assert.Contains(property.Modifiers, modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
            Assert.DoesNotContain(property.Modifiers, modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        }
    }

    [Fact]
    public void AnalyzeAndFindReferences_PublishTheirTypedArguments()
    {
        var root = Parse(ToolsPath);

        AssertParameterTypes(root, "LifebloodAnalyzeProject", new Dictionary<string, string>
        {
            ["incremental"] = "bool",
            ["readOnly"] = "bool",
            ["allowFullFallback"] = "bool",
            ["defineProfiles"] = "string[]",
        });

        AssertParameterTypes(root, "LifebloodFindReferences", new Dictionary<string, string>
        {
            ["symbolId"] = "string",
            ["includeDeclarations"] = "bool",
        });

        AssertParameterTypes(root, "LifebloodContractAudit", new Dictionary<string, string>
        {
            ["manifestJson"] = "string",
            ["manifestPath"] = "string",
            ["profileScope"] = "string",
            ["moduleScope"] = "string",
            ["filePaths"] = "string[]",
            ["containingSymbolIds"] = "string[]",
            ["includeRuleIds"] = "string[]",
            ["maxFacts"] = "int?",
            ["maxFindings"] = "int?",
            ["maxEvidencePerFinding"] = "int?",
            ["summarize"] = "bool",
        });
    }

    [Fact]
    public void EveryBridgeTool_UsesThePollableResponseLifecycle()
    {
        var root = Parse(ToolsPath);
        var tools = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(HasMcpToolAttribute)
            .ToArray();

        Assert.Equal(19, tools.Length);
        foreach (var tool in tools)
        {
            var attribute = tool.AttributeLists.SelectMany(list => list.Attributes)
                .Single(IsMcpToolAttribute);
            var text = attribute.ToString();
            Assert.Contains("RequiresPolling = true", text);
            Assert.Contains("MaxPollSeconds = 360", text);

            var handler = tool.Members.OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == "HandleCommand");
            var invocationNames = handler.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(invocation => invocation.Expression.ToString())
                .ToArray();
            Assert.Contains(invocationNames, name =>
                name.EndsWith("CallToolWithPolling", StringComparison.Ordinal)
                || name.EndsWith("AnalyzeCurrentProjectWithPolling", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ContractAudit_TranslatesInlineManifestJsonBeforeForwarding()
    {
        var root = Parse(ToolsPath);
        var tool = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "LifebloodContractAudit");
        var handler = tool.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "HandleCommand");
        var source = handler.ToString();

        Assert.Contains("forwarded.Remove(\"manifestJson\")", source);
        Assert.Contains("forwarded[\"manifest\"] = JObject.Parse(manifestJson)", source);
        Assert.Contains("CallToolWithPolling(\"lifeblood_contract_audit\", forwarded)", source);
    }

    [Fact]
    public void PollingCoordinator_UsesArgumentsOnlyToDetectAdmissionConflicts()
    {
        var root = Parse(ClientPath);
        var bridge = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "LifebloodBridgeClient");
        var polling = bridge.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "CallToolWithPolling");
        var analysis = bridge.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "AnalyzeCurrentProjectWithPolling");

        Assert.Contains("var requestIdentity = forwarded.ToString(Formatting.None)", polling.ToString());
        Assert.Contains("_pendingCalls.Admit(", polling.ToString());
        var projectPathAssignment = analysis.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(assignment => assignment.Left.ToString() == "args[\"projectPath\"]");
        Assert.DoesNotContain(
            projectPathAssignment.Ancestors().TakeWhile(node => node != analysis),
            node => node is IfStatementSyntax);
        Assert.Contains("CallToolWithPolling(\"lifeblood_analyze\", args)", analysis.ToString());
    }

    [Fact]
    public void PollingCoordinator_StatusLookupUsesToolIdentityWithoutOriginalArguments()
    {
        var root = Parse(ClientPath);
        var bridge = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "LifebloodBridgeClient");
        var polling = bridge.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "CallToolWithPolling");
        var source = polling.ToString();

        Assert.DoesNotContain("CreateCallKey(toolName, forwarded)", source);
        Assert.Contains("PollToolCall(toolName)", source);
    }

    [Fact]
    public void BridgeLaunchesInstalledToolAsWorkspaceSharedProxy()
    {
        var source = File.ReadAllText(ClientPath);
        var root = Parse(ClientPath);
        var bridge = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "LifebloodBridgeClient");
        var ensureStarted = bridge.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "EnsureStarted");

        Assert.Contains("FileName = ServerCommand", ensureStarted.ToString());
        Assert.Contains("--shared --shared-key", ensureStarted.ToString());
        Assert.Contains("WorkingDirectory = unityRoot", ensureStarted.ToString());
        Assert.Contains("LIFEBLOOD_MCP_COMMAND", source);
        Assert.DoesNotContain("LIFEBLOOD_SERVER_DLL", source);
        Assert.DoesNotContain("Lifeblood.Server.Mcp.dll", source);
    }

    private static bool HasToolParameterAttribute(MemberDeclarationSyntax member)
        => member.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().EndsWith("ToolParameter", StringComparison.Ordinal));

    private static bool HasMcpToolAttribute(ClassDeclarationSyntax type)
        => type.AttributeLists.SelectMany(list => list.Attributes).Any(IsMcpToolAttribute);

    private static bool IsMcpToolAttribute(AttributeSyntax attribute)
        => attribute.Name.ToString().EndsWith("McpForUnityTool", StringComparison.Ordinal);

    private static void AssertParameterTypes(
        CompilationUnitSyntax root,
        string toolClassName,
        IReadOnlyDictionary<string, string> expected)
    {
        var tool = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == toolClassName);
        var parameters = tool.Members.OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "Parameters");
        var actual = parameters.Members.OfType<PropertyDeclarationSyntax>()
            .Where(HasToolParameterAttribute)
            .ToDictionary(
                property => property.Identifier.ValueText,
                property => property.Type.ToString(),
                StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    private static CompilationUnitSyntax Parse(string path)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
                current = current.Parent;
            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    private static string ToolsPath =>
        Path.Combine(RepoRoot, "unity", "Editor", "LifebloodBridge", "LifebloodTools.cs");

    private static string ClientPath =>
        Path.Combine(RepoRoot, "unity", "Editor", "LifebloodBridge", "LifebloodBridgeClient.cs");
}
