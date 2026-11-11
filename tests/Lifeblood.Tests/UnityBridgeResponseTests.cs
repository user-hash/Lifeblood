using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Lifeblood.Connectors.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class UnityBridgeResponseTests
{
    private static readonly Lazy<Assembly> Probe = new(CompileProbe);

    [Fact]
    public void CompletedInnerError_ProducesOuterFailureAndPreservesOriginalResult()
    {
        var original = JObject.Parse("""{"isError":true,"content":[{"type":"text","text":"Shared daemon build mismatch"}],"details":{"recoverable":true}}""");
        var result = Assert.IsType<JObject>(Invoke("Poll", original));
        Assert.Equal("ErrorResponse", (string?)result["responseType"]);
        Assert.False((bool)result["success"]!);
        Assert.Contains("Shared daemon build mismatch", (string?)result["error"]);
        Assert.True(JToken.DeepEquals(original, result["data"]));
        Assert.True((bool)result["consumed"]!);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"text","text":"ok"}]}""")]
    [InlineData("""{"isError":false,"content":[{"type":"text","text":"{\"success\":false,\"diagnostics\":[\"CS0123\"]}"}]}""")]
    [InlineData("""{"isError":false,"content":[],"structuredContent":{"success":false,"diagnostics":["CS0123"]}}""")]
    public void ValidToolResults_PreserveTransportSuccessEvenWhenCompilationFails(string json)
    {
        var original = JObject.Parse(json);
        var result = Assert.IsType<JObject>(Invoke("Poll", original));
        Assert.Equal("JObject", (string?)result["responseType"]);
        Assert.True(JToken.DeepEquals(original, result["data"]));
        Assert.True((bool)result["sameInstance"]!);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":7,"error":{"code":-32603,"message":"Shared daemon build mismatch"}}""", "Shared daemon build mismatch")]
    [InlineData("""{"jsonrpc":"2.0","id":8,"result":{}}""", "mismatched")]
    [InlineData("""{"jsonrpc":"2.0","id":7}""", "no JSON-RPC result")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"result":{"protocolVersion":"2024-11-05","serverInfo":{}}}""", "incomplete")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"result":{"protocolVersion":"unsupported","capabilities":{},"serverInfo":{}}}""", "unsupported")]
    public void Initialize_RejectsErrorsMismatchesAndIncompleteNegotiation(string line, string message)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => Invoke("Initialize", line));
        Assert.Contains(message, Assert.IsType<InvalidOperationException>(exception.InnerException).Message);
    }

    [Theory]
    [InlineData(McpProtocolSpec.SupportedVersion)]
    [InlineData(McpProtocolSpec.StructuredContentVersion)]
    public void Initialize_AcceptsCompleteMatchingNegotiation(string protocolVersion)
    {
        Assert.Equal(true, Invoke("Initialize", new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 7,
            ["result"] = new JObject { ["protocolVersion"] = protocolVersion, ["capabilities"] = new JObject(), ["serverInfo"] = new JObject { ["name"] = "lifeblood", ["version"] = "test" } },
        }.ToString()));
    }

    [Fact]
    public void StructuredInnerError_ProducesOuterFailureWithoutLosingOriginalPayload()
    {
        var original = JObject.Parse("""{"isError":true,"content":[],"structuredContent":{"failure":"file-scope-precondition","compilationStarted":false}}""");
        var result = Assert.IsType<JObject>(Invoke("Poll", original));
        Assert.False((bool)result["success"]!);
        Assert.Contains("file-scope-precondition", (string?)result["error"]);
        Assert.True(JToken.DeepEquals(original, result["data"]));
        Assert.True((bool)result["consumed"]!);
    }

    private static object? Invoke(string name, object argument)
        => Probe.Value.GetType("BridgeResponseProbe", true)!.GetMethod(name)!.Invoke(null, new[] { argument });

    private static Assembly CompileProbe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Lifeblood.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var bridge = Path.Combine(directory!.FullName, "unity", "Editor", "LifebloodBridge");
        var sources = new[] { "LifebloodBridgeClient.cs", "PendingToolCallRegistry.cs", "McpProtocolConstants.cs" }
            .Select(name => Path.Combine(bridge, name))
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .Append(CSharpSyntaxTree.ParseText(ProbeSource));
        var references = Assert.IsType<string>(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location).Distinct()
            .Where(path => !Path.GetFileName(path).StartsWith("Lifeblood.", StringComparison.Ordinal))
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create($"BridgeResponseProbe_{Guid.NewGuid():N}", sources, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        image.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(image);
    }

    private const string ProbeSource = """
        using System;
        using System.Reflection;
        using System.Threading.Tasks;
        using Newtonsoft.Json.Linq;
        using Lifeblood.UnityBridge;
        public static class BridgeResponseProbe {
            public static JObject Poll(JObject payload) {
                using var client = new LifebloodBridgeClient();
                var registry = (PendingToolCallRegistry<JObject>)typeof(LifebloodBridgeClient)
                    .GetField("_pendingCalls", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(client);
                registry.Admit("query", "arguments", () => Task.FromResult(payload), out _);
                var response = typeof(LifebloodBridgeClient).GetMethod("PollToolCall", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(client, new object[] { "query" });
                var error = response as MCPForUnity.Editor.Helpers.ErrorResponse;
                return new JObject {
                    ["responseType"] = response.GetType().Name,
                    ["success"] = error == null,
                    ["error"] = error?.Error,
                    ["data"] = (JObject)(error?.Data ?? response),
                    ["sameInstance"] = ReferenceEquals(payload, response),
                    ["consumed"] = registry.Poll("query", out _) == PendingCallPollState.Missing
                };
            }
            public static bool Initialize(string line) { LifebloodBridgeClient.ValidateInitializeResponse(line, 7); return true; }
        }
        namespace UnityEngine {
            public static class Application { public static string dataPath => "workspace/Assets"; }
            public static class Debug { public static void Log(object message) {} }
        }
        namespace UnityEditor {
            public sealed class InitializeOnLoadMethodAttribute : Attribute {}
            public static class EditorPrefs { public static string GetString(string name, string fallback) => fallback; }
            public static class AssemblyReloadEvents { public static event Action beforeAssemblyReload; }
            public static class EditorApplication { public static event Action quitting; }
        }
        namespace MCPForUnity.Editor.Helpers {
            public sealed class ErrorResponse {
                public ErrorResponse(string error, object data = null) { Error = error; Data = data; }
                public string Error { get; }
                public object Data { get; }
            }
            public sealed class PendingResponse { public PendingResponse(string message, double pollIntervalSeconds) {} }
        }
        """;
}
