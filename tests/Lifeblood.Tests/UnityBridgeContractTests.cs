using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Lifeblood.Server.Mcp;
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
            Assert.True(
                owner.Identifier.ValueText is "Parameters" or "SnapshotReadParameters",
                $"Tool parameter '{property.Identifier.ValueText}' must live on a nested Parameters type or the shared snapshot-read base.");
            Assert.Contains(property.Modifiers, modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
            Assert.DoesNotContain(property.Modifiers, modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        }
    }

    [Fact]
    public void EveryBridgeTool_PublishesTheServerInputContractArguments()
    {
        var root = Parse(ToolsPath);
        var wrappers = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(HasMcpToolAttribute)
            .ToArray();
        var parameterTypes = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(type => type.Parent is CompilationUnitSyntax or NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax)
            .ToDictionary(type => type.Identifier.ValueText, StringComparer.Ordinal);

        Assert.Equal(19, wrappers.Length);
        foreach (var wrapper in wrappers)
        {
            var unityToolName = ReadMcpToolName(wrapper);
            var serverToolName = unityToolName == "lifeblood_analyze_project"
                ? "lifeblood_analyze"
                : unityToolName;
            var expected = ExpectedUnityArguments(serverToolName);
            var actual = ReadUnityArguments(wrapper, parameterTypes);

            Assert.Equal(expected, actual);
        }
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
            Assert.DoesNotContain("MaxPollSeconds", text);

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
    public void PollingCoordinator_DoesNotImposeAFixedToolCallDeadline()
    {
        var source = File.ReadAllText(ClientPath);

        Assert.DoesNotContain("ToolCallTimeoutMs", source);
        Assert.DoesNotContain("Response timed out", source);
        Assert.DoesNotContain("killing server", source);
        Assert.Contains("ReadLineUntilProcessExit(_stdout)", source);
        Assert.Contains("ReadLineWithTimeout(_stdout, InitTimeoutMs)", source);
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

    private static string ReadMcpToolName(ClassDeclarationSyntax tool)
        => tool.AttributeLists.SelectMany(list => list.Attributes)
            .Single(IsMcpToolAttribute)
            .ArgumentList!.Arguments[0]
            .Expression
            .ToString()
            .Trim('"');

    private static IReadOnlyDictionary<string, string> ExpectedUnityArguments(string serverToolName)
    {
        var definition = ToolRegistry.GetDefinitions().Single(d => d.Name == serverToolName);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in definition.InputContract.ArgumentList)
        {
            if (serverToolName == "lifeblood_analyze"
                && argument.Name is "projectPath" or "graphPath")
            {
                continue;
            }

            if (serverToolName == "lifeblood_contract_audit"
                && argument.Name == "manifest")
            {
                expected["manifestJson"] = "string:false";
                continue;
            }

            expected[argument.Name] = ToUnityType(argument) + ":" + argument.Required.ToString().ToLowerInvariant();
        }

        return expected;
    }

    private static string ToUnityType(ToolArgumentContract argument)
        => argument.Name == "expectedAnalysisGeneration" ? "long?"
            : argument.Type switch
            {
                ToolArgumentType.String => "string",
                ToolArgumentType.Integer => argument.Required ? "int" : "int?",
                ToolArgumentType.Number => argument.Required ? "double" : "double?",
                ToolArgumentType.Boolean => "bool",
                ToolArgumentType.Array => argument.ArrayItemType == ToolArgumentType.String
                    ? "string[]"
                    : "object[]",
                ToolArgumentType.Object => "object",
                _ => "object",
            };

    private static IReadOnlyDictionary<string, string> ReadUnityArguments(
        ClassDeclarationSyntax tool,
        IReadOnlyDictionary<string, ClassDeclarationSyntax> parameterTypes)
    {
        var parameters = tool.Members.OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "Parameters");
        return EnumerateParameterProperties(parameters, parameterTypes)
            .ToDictionary(
                property => property.Identifier.ValueText,
                property => property.Type.ToString() + ":" + IsRequired(property).ToString().ToLowerInvariant(),
                StringComparer.Ordinal);
    }

    private static IEnumerable<PropertyDeclarationSyntax> EnumerateParameterProperties(
        ClassDeclarationSyntax parameterType,
        IReadOnlyDictionary<string, ClassDeclarationSyntax> parameterTypes)
    {
        if (parameterType.BaseList?.Types.FirstOrDefault()?.Type.ToString() is { Length: > 0 } baseName
            && parameterTypes.TryGetValue(baseName, out var baseType))
        {
            foreach (var property in EnumerateParameterProperties(baseType, parameterTypes))
                yield return property;
        }

        foreach (var property in parameterType.Members.OfType<PropertyDeclarationSyntax>().Where(HasToolParameterAttribute))
            yield return property;
    }

    private static bool IsRequired(PropertyDeclarationSyntax property)
    {
        var attribute = property.AttributeLists.SelectMany(list => list.Attributes)
            .Single(attribute => attribute.Name.ToString().EndsWith("ToolParameter", StringComparison.Ordinal));
        foreach (var argument in attribute.ArgumentList?.Arguments ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>))
        {
            if (argument.NameEquals?.Name.Identifier.ValueText == "Required"
                && argument.Expression.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                return false;
            }
        }

        return true;
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
