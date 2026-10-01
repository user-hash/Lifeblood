using System.Text.Json;
using System.Text.Json.Nodes;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public class McpCanonicalIdentityWireTests
{
    [Fact]
    public void TwoDispatchersNegotiateIndependentlyAndShareCanonicalPublication()
    {
        using var fixture = new Fixture();
        var legacy = fixture.Host.CreateDispatcher();
        var canonical = fixture.Host.CreateDispatcher();
        Assert.Equal(McpProtocolSpec.SchemaVersions.Legacy, Initialize(legacy).SchemaVersion);
        Assert.Equal(McpProtocolSpec.SchemaVersions.Canonical, Initialize(canonical, McpProtocolSpec.SchemaVersions.Canonical).SchemaVersion);
        var analyze = Tool(legacy, "lifeblood_analyze", new { projectPath = fixture.Root });
        Assert.NotEqual(true, analyze.Result.IsError);
        var old = Tool(legacy, "lifeblood_lookup", new { symbolId = "type:App.C" });
        var current = Tool(canonical, "lifeblood_lookup", new { symbolId = "type:App.C" });
        Assert.Equal("type:App.C", old.Payload["id"]!.GetValue<string>());
        Assert.StartsWith("type:v2|", current.Payload["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(old.Payload["envelope"]!["snapshotId"]!.GetValue<string>(), current.Payload["envelope"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal("csharp-canonical-v2", fixture.Host.Session.CurrentSnapshot.Identity!.Spec.DeclarationIdentityPolicy);
        var ambiguous = Tool(canonical, "lifeblood_lookup", new { symbolId = "method:App.C.M(int)" });
        Assert.True(ambiguous.Result.IsError);
        Assert.Equal(2, ambiguous.Payload["candidates"]!.AsArray().Count);
        var oldAmbiguous = Tool(legacy, "lifeblood_lookup", new { symbolId = "method:App.C.M(int)" });
        Assert.True(oldAmbiguous.Result.IsError);
        Assert.Equal("legacy-canonical-id-ambiguity", oldAmbiguous.Payload["failure"]!.GetValue<string>());
        Assert.Equal(2, oldAmbiguous.Payload["canonicalCandidates"]!.AsArray().Count);
        Assert.DoesNotContain("legacyProjection", JsonSerializer.Serialize(old.Result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchProjectionsPreserveOrderSnapshotAndPerChildErrors()
    {
        using var fixture = new Fixture();
        var legacy = fixture.Host.CreateDispatcher();
        var canonical = fixture.Host.CreateDispatcher();
        Initialize(legacy); Initialize(canonical, McpProtocolSpec.SchemaVersions.Canonical);
        Tool(canonical, "lifeblood_analyze", new { projectPath = fixture.Root });
        var batch = Tool(legacy, "lifeblood_batch", new { calls = new object[] {
            new { tool = "lifeblood_lookup", arguments = new { symbolId = "type:App.C" } },
            new { tool = "lifeblood_lookup", arguments = new { symbolId = "method:App.C.M(int)" } },
            new { tool = "lifeblood_capabilities", arguments = new { } },
        } });
        Assert.NotEqual(true, batch.Result.IsError);
        var rows = batch.Payload["results"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.False(rows[0]!["isError"]!.GetValue<bool>());
        Assert.True(rows[1]!["isError"]!.GetValue<bool>());
        Assert.False(rows[2]!["isError"]!.GetValue<bool>());
        var first = JsonNode.Parse(rows[0]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("type:App.C", first["id"]!.GetValue<string>());
        Assert.Equal(batch.Payload["snapshotId"]!.GetValue<string>(), first["envelope"]!["snapshotId"]!.GetValue<string>());
        var ids = fixture.Host.Session.Graph!.Symbols.Where(s => s.Name == "M" && s.Kind == Lifeblood.Domain.Graph.SymbolKind.Method).Select(s => s.Id).ToArray();
        var references = Tool(canonical, "lifeblood_find_references", new { symbolIds = ids });
        Assert.NotEqual(true, references.Result.IsError);
        Assert.Equal(2, references.Payload["results"]!.AsArray().Count);
        foreach (var row in references.Payload["results"]!.AsArray())
            Assert.Single(row!["locations"]!.AsArray());
        var unique = fixture.Host.Session.Graph.Symbols.Single(s => s.Name == "Unique");
        var rename = Tool(canonical, "lifeblood_rename", new { symbolId = unique.Id, newName = "Renamed" });
        Assert.NotEqual(true, rename.Result.IsError);
        Assert.Equal(Fixture.Source, System.IO.File.ReadAllText(fixture.SourcePath));
    }

    [Fact]
    public void LegacyProjectionHandlesExternalTypesAndPreservesSourceAndDocumentationText()
    {
        using var fixture = new Fixture();
        var legacy = fixture.Host.CreateDispatcher(); Initialize(legacy);
        Tool(legacy, "lifeblood_analyze", new { projectPath = fixture.Root });
        var line = Fixture.Source.Split('\n')[3];
        var position = Tool(legacy, "lifeblood_symbol_at_position", new { filePath = fixture.SourcePath, line = 4, column = line.IndexOf("int", StringComparison.Ordinal) + 1 });
        Assert.NotEqual(true, position.Result.IsError);
        Assert.Equal("type:System.Int32", position.Payload["symbolId"]!.GetValue<string>());
        var docs = Assert.IsType<McpToolResult>(legacy.Dispatch(Request(McpProtocolSpec.Methods.ToolsCall, new { name = "lifeblood_documentation", arguments = new { symbolId = "method:App.C.Unique(int)" } }))!.Result);
        Assert.Contains("type:v2|literal", docs.Content[0].Text, StringComparison.Ordinal);
        var unknown = legacy.Dispatch(Request(McpProtocolSpec.Methods.Initialize,
            new Dictionary<string, object> { [McpProtocolSpec.SchemaVersions.InitializeParameter] = "v3" }));
        Assert.Equal(-32602, unknown!.Error!.Code);
        Assert.Equal("type:App.C", Tool(legacy, "lifeblood_lookup", new { symbolId = "type:App.C" }).Payload["id"]!.GetValue<string>());
    }

    [Fact]
    public void PositionReceiptsPreserveNegotiatedIdsAndSnapshotOnSuccessAndFailure()
    {
        using var fixture = new Fixture(crossAssembly: true);
        var legacy = fixture.Host.CreateDispatcher();
        var canonical = fixture.Host.CreateDispatcher();
        Initialize(legacy); Initialize(canonical, McpProtocolSpec.SchemaVersions.Canonical);
        Tool(canonical, "lifeblood_analyze", new { projectPath = fixture.Root });
        foreach (var dispatcher in new[] { legacy, canonical })
        {
            var success = Tool(dispatcher, "lifeblood_symbol_at_position",
                new { filePath = fixture.SourcePath, line = 2, column = 14, moduleName = "App" });
            Assert.NotEqual(true, success.Result.IsError);
            Assert.Equal("C", success.Payload["name"]!.GetValue<string>());
            Assert.Equal(dispatcher == legacy ? "type:App.C" : fixture.Host.Session.Graph!.Symbols.Single(s => s.Name == "C").Id,
                success.Payload["symbolId"]!.GetValue<string>());
            var resolution = success.Payload["positionResolution"]!;
            Assert.Equal("Resolved", resolution["outcome"]!.GetValue<string>());
            Assert.Equal("App", resolution["fileOwnership"]!["resolvedModule"]!.GetValue<string>());
            var invalid = Tool(dispatcher, "lifeblood_symbol_at_position",
                new { filePath = fixture.SourcePath, line = 1, column = int.MaxValue });
            Assert.True(invalid.Result.IsError);
            Assert.True(invalid.Payload["error"]!.GetValue<bool>());
            Assert.Equal("ColumnOutOfRange", invalid.Payload["positionResolution"]!["coordinateFailure"]!.GetValue<string>());
            Assert.Equal(success.Payload["envelope"]!["snapshotId"]!.GetValue<string>(),
                invalid.Payload["snapshotId"]!.GetValue<string>());
            Assert.Equal(success.Payload["analyzedUnderProfile"]!.GetValue<string>(),
                invalid.Payload["analyzedUnderProfile"]!.GetValue<string>());
            var pinnedMiss = Tool(dispatcher, "lifeblood_symbol_at_position",
                new { filePath = fixture.SourcePath, line = 2, column = 14, moduleName = "Library" });
            Assert.True(pinnedMiss.Result.IsError);
            Assert.Equal("NotInModule", pinnedMiss.Payload["positionResolution"]!["fileOwnership"]!["outcome"]!.GetValue<string>());
        }
    }

    [Fact]
    public void LinkedPositionReturnsCandidatesUntilModuleIsPinned()
    {
        using var fixture = new Fixture(crossAssembly: true, linkedSource: true);
        var dispatcher = fixture.Host.CreateDispatcher();
        Initialize(dispatcher, McpProtocolSpec.SchemaVersions.Canonical);
        Assert.NotEqual(true, Tool(dispatcher, "lifeblood_analyze", new { projectPath = fixture.Root }).Result.IsError);
        var ambiguous = Tool(dispatcher, "lifeblood_symbol_at_position", new { filePath = fixture.SourcePath, line = 2, column = 14 });
        Assert.True(ambiguous.Result.IsError);
        var ownership = ambiguous.Payload["positionResolution"]!["fileOwnership"]!;
        Assert.Equal("Ambiguous", ownership["outcome"]!.GetValue<string>());
        Assert.Equal(new[] { "App", "Library" }, ownership["candidateModules"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Single(ownership["candidateFilePaths"]!.AsArray());
        var addresses = new List<string>();
        foreach (var module in new[] { "App", "Library" })
        {
            var pinned = Tool(dispatcher, "lifeblood_symbol_at_position",
                new { filePath = fixture.SourcePath, line = 2, column = 14, moduleName = module });
            Assert.NotEqual(true, pinned.Result.IsError);
            Assert.Equal(module, pinned.Payload["positionResolution"]!["fileOwnership"]!["resolvedModule"]!.GetValue<string>());
            Assert.Equal(ambiguous.Payload["snapshotId"]!.GetValue<string>(), pinned.Payload["envelope"]!["snapshotId"]!.GetValue<string>());
            addresses.Add(pinned.Payload["symbolId"]!.GetValue<string>());
        }
        Assert.NotEqual(addresses[0], addresses[1]);
    }

    [Fact]
    public void SourceAndMetadataReplicasUseOneAddressForReferencesDefinitionAndRename()
    {
        using var fixture = new Fixture(crossAssembly: true);
        var legacy = fixture.Host.CreateDispatcher();
        var canonical = fixture.Host.CreateDispatcher();
        Initialize(legacy); Initialize(canonical, McpProtocolSpec.SchemaVersions.Canonical);
        Tool(canonical, "lifeblood_analyze", new { projectPath = fixture.Root });
        const string predecessor = "method:Library.Lib.Touch(int)";
        var symbol = fixture.Host.Session.Graph!.Symbols.Single(s => s.Name == "Touch");
        foreach (var dispatcher in new[] { legacy, canonical })
        {
            var references = Tool(dispatcher, "lifeblood_find_references", new { symbolId = predecessor });
            Assert.NotEqual(true, references.Result.IsError);
            Assert.Equal(1, references.Payload["count"]!.GetValue<int>());
            var definition = Tool(dispatcher, "lifeblood_find_definition", new { symbolId = symbol.Id });
            Assert.NotEqual(true, definition.Result.IsError);
            Assert.EndsWith("Lib.cs", definition.Payload["definition"]!["filePath"]!.GetValue<string>(), StringComparison.Ordinal);
            var rename = Tool(dispatcher, "lifeblood_rename", new { symbolId = predecessor, newName = "Changed" });
            Assert.NotEqual(true, rename.Result.IsError);
            Assert.True(rename.Payload["editCount"]!.GetValue<int>() == 2, rename.Payload.ToJsonString());
            Assert.Equal(dispatcher == legacy ? predecessor : symbol.Id, rename.Payload["symbolId"]!.GetValue<string>());
        }
        Assert.Contains("Touch(1)", System.IO.File.ReadAllText(System.IO.Path.Combine(fixture.Root, "Use.cs")), StringComparison.Ordinal);
        Assert.Contains("Touch(int x)", System.IO.File.ReadAllText(System.IO.Path.Combine(fixture.Root, "Library", "Lib.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void InlineAndFileManifestsAdmitTheSamePredecessorAddresses()
    {
        using var fixture = new Fixture();
        var canonical = fixture.Host.CreateDispatcher();
        Initialize(canonical, McpProtocolSpec.SchemaVersions.Canonical);
        Tool(canonical, "lifeblood_analyze", new { projectPath = fixture.Root });
        var manifest = new { id = "wire-policy", version = "1", callRoutes = new[] {
            new { id = "entry", rootSymbolIds = new[] { "method:App.C.Run()" } } },
            externalApiCosts = new[] { new { id = "unique-cost", targetSymbolIds = new[] { "method:App.C.Unique(int)" },
                callRouteIds = new[] { "entry" }, categories = new[] { "RealtimeForbidden" }, annotationSource = "wire fixture policy" } } };
        var path = System.IO.Path.Combine(fixture.Root, "policy.json");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        var inline = Tool(canonical, "lifeblood_contract_audit", new { manifest });
        var file = Tool(canonical, "lifeblood_contract_audit", new { manifestPath = path });
        Assert.NotEqual(true, inline.Result.IsError);
        Assert.NotEqual(true, file.Result.IsError);
        var roots = inline.Payload["callRoutes"]![0]!["rootSymbolIds"]!.AsArray();
        Assert.StartsWith("method:v2|", Assert.Single(roots)!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(inline.Payload["callRoutes"]!.ToJsonString(), file.Payload["callRoutes"]!.ToJsonString());
        Assert.Equal(JsonSerializer.Serialize(manifest), System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void LegacyProjectionRejectsUnavailableAddressesWithoutRewritingOpaqueText()
    {
        var result = new McpToolResult { Content = new[] { new McpContent {
            Type = "text", Text = """{"symbolId":"method:v2|unavailable","sourceCode":"type:v2|literal"}""" } } };
        McpCanonicalIdCompatibility.PrepareLegacyResult("lifeblood_lookup", result, null, _ => null, new JsonSerializerOptions());
        Assert.True(result.LegacyProjection!.IsError);
        var failure = JsonNode.Parse(result.LegacyProjection.Content[0].Text)!;
        Assert.Equal("legacy-canonical-id-unavailable", failure["failure"]!.GetValue<string>());
        Assert.Equal("method:v2|unavailable", failure["unavailableCanonicalIds"]![0]!.GetValue<string>());
        Assert.Contains("type:v2|literal", result.Content[0].Text, StringComparison.Ordinal);
        var imported = new McpToolResult { Content = new[] { new McpContent {
            Type = "text", Text = """{"targetId":"opaque|imported-external"}""" } } };
        McpCanonicalIdCompatibility.PrepareLegacyResult("lifeblood_dependencies", imported, null, null, JsonSerializerOptions.Default, projectUnresolvedAddresses: false);
        Assert.NotEqual(true, imported.LegacyProjection!.IsError);
        Assert.Equal("opaque|imported-external", JsonNode.Parse(imported.LegacyProjection.Content[0].Text)!["targetId"]!.GetValue<string>());

    }

    private static McpInitializeResult Initialize(McpDispatcher dispatcher, string? version = null)
        => Assert.IsType<McpInitializeResult>(dispatcher.Dispatch(Request(McpProtocolSpec.Methods.Initialize,
            version == null ? new Dictionary<string, object>() : new Dictionary<string, object> { [McpProtocolSpec.SchemaVersions.InitializeParameter] = version }))!.Result);

    private static (McpToolResult Result, JsonObject Payload) Tool(McpDispatcher dispatcher, string name, object arguments)
    {
        var rpc = dispatcher.Dispatch(Request(McpProtocolSpec.Methods.ToolsCall, new { name, arguments }));
        Assert.Null(rpc!.Error);
        var result = Assert.IsType<McpToolResult>(rpc.Result);
        Assert.True(result.Content[0].Text.StartsWith("{", StringComparison.Ordinal), result.Content[0].Text);
        return (result, JsonNode.Parse(result.Content[0].Text)!.AsObject());
    }

    private static JsonRpcRequest Request(string method, object parameters)
        => new() { Id = JsonSerializer.SerializeToElement(1), Method = method, Params = JsonSerializer.SerializeToElement(parameters) };

    private sealed class Fixture : IDisposable
    {
        internal const string Source = """
            namespace App;
            public class C {
                /// <summary>Preserve type:v2|literal in documentation.</summary>
                public void Unique(int x) {}
                public void M(int x) {}
                public void M(ref int x) {}
                public void Run() { int x = 0; Unique(1); M(1); M(ref x); _ = "type:v2|literal"; }
            }
            """;
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lifeblood-wire-" + Guid.NewGuid().ToString("N"));
        internal string SourcePath => System.IO.Path.Combine(Root, "App.cs");
        internal McpServerHost Host { get; }
        internal Fixture(bool crossAssembly = false, bool linkedSource = false)
        {
            System.IO.Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
            if (crossAssembly)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Root, "Library"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "Library", "Library.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
                System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "Library", "Lib.cs"), "namespace Library; public class Lib { public void Touch(int x) {} }");
                if (linkedSource)
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "Library", "Library.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Lib.cs"/><Compile Include="../App.cs"/></ItemGroup></Project>""");
                System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="App.cs"/><Compile Include="Use.cs"/><ProjectReference Include="Library/Library.csproj"/></ItemGroup></Project>""");
                System.IO.File.WriteAllText(System.IO.Path.Combine(Root, "Use.cs"), "namespace App; public class Use { public void Run(Library.Lib value) { value.Touch(1); } }");
            }
            System.IO.File.WriteAllText(SourcePath, Source);
            Host = McpServerHost.Create(ToolJsonCompatibilityMode.Legacy);
        }
        public void Dispose() { Host.Dispose(); System.IO.Directory.Delete(Root, recursive: true); }
    }
}
