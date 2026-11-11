using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Analysis;
using Lifeblood.Application.Ports.Analysis;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Application.UseCases;
using Lifeblood.Connectors.Mcp;
using Lifeblood.Domain.Graph;
using Lifeblood.Domain.Results;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

public sealed class FileScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lifeblood-file-scope-" + Guid.NewGuid().ToString("N"));
    private readonly HookFileSystem _fs = new();

    public FileScopeTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Scope.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
            <ItemGroup><Compile Include="A.cs" /><Compile Include="B.cs" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { public int Value => 1; }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "public class B { public int Value => 2; }");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Capture_IsNonmutatingBoundedContentEvidence_WithExplicitMissingFiles()
    {
        using var session = Load();
        var handler = Handler(session);
        var snapshot = session.CurrentSnapshot;
        var receipt = Capture(handler, "owner-a", "A.cs", "New.cs");
        Assert.Same(snapshot, session.CurrentSnapshot);
        Assert.Equal("owner-a", receipt.GetProperty("ownerId").GetString());
        var files = receipt.GetProperty("files");
        Assert.Equal("sha256_" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, "A.cs")))) , files[0].GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("sha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("byteLength").ValueKind);
        Assert.Equal(0, _fs.Writes);
        var verify = Call(handler, "lifeblood_file_scope", new { action = "verify", ownerId = "owner-a", receipt });
        Assert.False(verify.Result.IsError);
        Assert.Equal("matched", verify.Json.GetProperty("status").GetString());
        Assert.False(verify.Json.GetProperty("semanticInputsVerified").GetBoolean());
    }

    [Fact]
    public void UnrelatedOwnerEdit_DoesNotInvalidateScopeOrForceRefresh()
    {
        using var session = Load();
        var handler = Handler(session);
        var first = Capture(handler, "owner-a", "A.cs");
        var second = Capture(handler, "owner-b", "B.cs");
        var snapshot = session.SnapshotId;
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { public int Value => 9; }");
        Assert.True(Call(handler, "lifeblood_file_scope", new { action = "verify", ownerId = "owner-a", receipt = first }).Result.IsError);
        Assert.False(Call(handler, "lifeblood_file_scope", new { action = "verify", ownerId = "owner-b", receipt = second }).Result.IsError);
        var check = Call(handler, "lifeblood_compile_check", new { filePath = "B.cs", expectedFileScope = second });
        Assert.NotEqual(true, check.Result.IsError);
        Assert.True(check.Json.GetProperty("success").GetBoolean());
        Assert.Equal("hashVerifiedCapturedBytes", check.Json.GetProperty("preconditions").GetProperty("sourceAuthority").GetString());
        Assert.Equal(snapshot, session.SnapshotId);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("create")]
    public void OverlapChanges_FailBeforeCompilationOrRefresh(string change)
    {
        using var session = Load();
        var handler = Handler(session);
        var path = change == "create" ? "New.cs" : "A.cs";
        var receipt = Capture(handler, "owner", path);
        var snapshot = session.SnapshotId;
        var fullPath = Path.Combine(_root, path);
        if (change == "delete") File.Delete(fullPath);
        else
        {
            var before = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.UtcNow;
            File.WriteAllText(fullPath, "public class A { public int Value => 8; }");
            File.SetLastWriteTimeUtc(fullPath, before);
        }
        var check = Call(handler, "lifeblood_compile_check", new { filePath = path, expectedFileScope = receipt });
        Assert.True(check.Result.IsError);
        Assert.Equal("file-scope-precondition", check.Json.GetProperty("failure").GetString());
        Assert.False(check.Json.GetProperty("compilationStarted").GetBoolean());
        Assert.Single(check.Json.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(snapshot, session.SnapshotId);
    }

    [Fact]
    public void FileScopeSurvivesUnrelatedNewPublication_ExactPublicationPreconditionDoesNot()
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", "A.cs");
        var oldId = session.SnapshotId.ToString();
        var oldGeneration = session.AnalysisGeneration;
        File.WriteAllText(Path.Combine(_root, "B.cs"), "public class B { public int Value => 5; }");
        session.Load(_root, null, null);
        Assert.NotEqual(oldId, session.SnapshotId.ToString());
        Assert.NotEqual(true, Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt }).Result.IsError);
        _fs.Reads = 0;
        var mismatch = Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedSnapshotId = oldId, expectedAnalysisGeneration = oldGeneration });
        Assert.True(mismatch.Result.IsError);
        Assert.Equal("snapshot-precondition", mismatch.Json.GetProperty("failure").GetString());
        Assert.Equal(0, _fs.Reads);
    }

    [Fact]
    public void Check_UsesHashedBytesEvenWhenExternalFileChangesAfterCapture()
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", "A.cs");
        _fs.AfterOpen = path => File.WriteAllText(path, "this is now invalid source");
        var check = Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt });
        Assert.NotEqual(true, check.Result.IsError);
        Assert.True(check.Json.GetProperty("success").GetBoolean());
        Assert.Contains("invalid source", File.ReadAllText(Path.Combine(_root, "A.cs")));
        Assert.Equal("hashVerifiedCapturedBytes", check.Json.GetProperty("preconditions").GetProperty("sourceAuthority").GetString());
        Assert.False(check.Json.GetProperty("preconditions").GetProperty("semanticInputsVerified").GetBoolean());
    }

    [Fact]
    public void GuardedCheck_EmptyCapturedFileDoesNotReuseTheRetainedDeclaration()
    {
        File.WriteAllText(Path.Combine(_root, "B.cs"), "public class B { public int Read() => new A().Value; }");
        using var session = Load();
        var handler = Handler(session);
        var snapshot = session.CurrentSnapshot;
        File.WriteAllText(Path.Combine(_root, "A.cs"), "");
        var receipt = Capture(handler, "owner", "A.cs");
        Assert.Equal(0, receipt.GetProperty("files")[0].GetProperty("byteLength").GetInt64());

        var check = Call(handler, "lifeblood_compile_check", new
        {
            filePath = "A.cs", expectedFileScope = receipt,
            expectedSnapshotId = session.SnapshotId.ToString(), expectedAnalysisGeneration = session.AnalysisGeneration,
        });

        Assert.NotEqual(true, check.Result.IsError);
        Assert.False(check.Json.GetProperty("success").GetBoolean());
        Assert.Contains(check.Json.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("id").GetString() == "CS0246"
            && diagnostic.GetProperty("filePath").GetString()!.EndsWith("B.cs", StringComparison.Ordinal));
        Assert.Equal("hashVerifiedCapturedBytes", check.Json.GetProperty("preconditions").GetProperty("sourceAuthority").GetString());
        Assert.Same(snapshot, session.CurrentSnapshot);
    }

    [Fact]
    public void GuardedBatch_UsesBOMAwareCapturedBytes_AndRejectsOneConflictAsAWhole()
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { }", Encoding.Unicode);
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", "A.cs", "B.cs");
        var check = Call(handler, "lifeblood_compile_check", new { filePaths = new[] { "A.cs", "B.cs" }, expectedFileScope = receipt });
        Assert.True(check.Json.GetProperty("success").GetBoolean());
        Assert.Equal(2, check.Json.GetProperty("preconditions").GetProperty("checkedFileCount").GetInt32());
        Assert.Equal("pinnedSession", check.Json.GetProperty("staleRefresh").GetProperty("mode").GetString());
        File.WriteAllText(Path.Combine(_root, "B.cs"), "public class Changed { }");
        var conflict = Call(handler, "lifeblood_compile_check", new { filePaths = new[] { "A.cs", "B.cs" }, expectedFileScope = receipt });
        Assert.True(conflict.Result.IsError);
        Assert.False(conflict.Json.GetProperty("compilationStarted").GetBoolean());
    }

    [Fact]
    public async Task PublicationPreconditionAndCompilation_AreInsideTheSameWriterGate()
    {
        using var session = Load();
        using var gate = new GraphSessionGate(session);
        var handler = Handler(session, gate);
        var receipt = Capture(handler, "owner", "A.cs");
        var oldId = session.SnapshotId.ToString();
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var refreshAttempt = new ManualResetEventSlim();
        using var refreshEntered = new ManualResetEventSlim();
        _fs.AfterOpen = _ => { reading.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var check = Task.Run(() => Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt, expectedSnapshotId = oldId }));
        Task<int>? refresh = null;
        try
        {
            Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
            refresh = Task.Run(() => { refreshAttempt.Set(); return gate.Write(() => { refreshEntered.Set(); return 1; }); });
            Assert.True(refreshAttempt.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(refreshEntered.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally { release.Set(); }
        var completed = await check;
        if (refresh != null) await refresh;
        Assert.True(completed.Json.GetProperty("success").GetBoolean());
        Assert.Equal(oldId, completed.Json.GetProperty("envelope").GetProperty("snapshotId").GetString());
        Assert.True(refreshEntered.IsSet);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("workspace")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("null")]
    public void MalformedReceipts_FailClosedEvenInLegacyJsonMode(string damage)
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = JsonNode.Parse(Capture(handler, "owner", "A.cs").GetRawText())!;
        switch (damage)
        {
            case "hash": receipt["files"]![0]!["sha256"] = "not-a-hash"; break;
            case "version": receipt["schemaVersion"] = 999; break;
            case "workspace": receipt["workspaceRoot"] = Path.GetTempPath(); break;
            case "duplicate": receipt["files"]!.AsArray().Add(receipt["files"]![0]!.DeepClone()); break;
            case "unknown": receipt["extra"] = true; break;
            case "null": receipt = null; break;
        }
        var check = Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt });
        Assert.True(check.Result.IsError);
        Assert.Equal(damage == "null" ? "tool-argument-contract" : "file-scope-precondition-invalid", check.Json.GetProperty("failure").GetString());
        Assert.Equal(0, _fs.Writes);
    }

    [Fact]
    public void UncoveredCompileFileAndExplicitRefresh_AreRejected()
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", "A.cs");
        Assert.True(Call(handler, "lifeblood_compile_check", new { filePath = "B.cs", expectedFileScope = receipt }).Result.IsError);
        var refresh = Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt, staleRefresh = true });
        Assert.True(refresh.Result.IsError);
        Assert.Equal("compile-check-precondition-invalid", refresh.Json.GetProperty("failure").GetString());
        Assert.True(Call(handler, "lifeblood_compile_check", new { code = "class C {}", expectedFileScope = receipt }).Result.IsError);
    }

    [Theory]
    [InlineData("malformedPaths")]
    [InlineData("multipleSources")]
    [InlineData("malformedRefresh")]
    [InlineData("malformedFilePath")]
    [InlineData("malformedModule")]
    public void GuardedMalformedTargets_AreNotSilentlyDropped(string mode)
    {
        using var session = Load();
        var handler = Handler(session);
        var request = new JsonObject
        {
            ["expectedFileScope"] = JsonNode.Parse(Capture(handler, "owner", "A.cs").GetRawText()),
            ["filePath"] = "A.cs",
        };
        if (mode == "malformedPaths") { request.Remove("filePath"); request["filePaths"] = new JsonArray("A.cs", null); }
        else if (mode == "multipleSources") request["filePaths"] = new JsonArray("A.cs");
        else if (mode == "malformedRefresh") request["staleRefresh"] = "false";
        else if (mode == "malformedFilePath") { request["filePath"] = 123; request["filePaths"] = new JsonArray("A.cs"); }
        else request["moduleName"] = 123;
        _fs.Reads = 0;
        var check = Call(handler, "lifeblood_compile_check", request);
        Assert.True(check.Result.IsError);
        Assert.Equal(0, _fs.Reads);
        if (check.Json.TryGetProperty("compilationStarted", out var started)) Assert.False(started.GetBoolean());
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("")]
    public void ScopePaths_RejectOutsideWorkspaceAndEmptyPaths(string path)
    {
        using var session = Load();
        Assert.True(Call(Handler(session), "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = new[] { path } }).Result.IsError);
    }

    [Fact]
    public void ScopeLimitsAndUnreadableFiles_FailWithoutPartialReceipts()
    {
        using var session = Load();
        var handler = Handler(session);
        File.WriteAllBytes(Path.Combine(_root, "Large.bin"), new byte[FileScopeObservation.MaximumFileBytes + 1]);
        Assert.True(Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = new[] { "Large.bin" } }).Result.IsError);
        Assert.True(Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = Enumerable.Range(0, 33).Select(i => $"{i}.cs").ToArray() }).Result.IsError);
        _fs.OpenError = new UnauthorizedAccessException("Denied");
        var denied = Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = new[] { "A.cs" } });
        Assert.True(denied.Result.IsError);
        Assert.False(denied.Json.TryGetProperty("receipt", out _));
    }

    [Fact]
    public void CancellationDuringCapture_StopsGuardedCheckWithoutPublishing()
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", "A.cs");
        var snapshot = session.SnapshotId;
        using var cancellation = new CancellationTokenSource();
        _fs.AfterOpen = _ => cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => handler.Handle("lifeblood_compile_check",
            JsonSerializer.SerializeToElement(new { filePath = "A.cs", expectedFileScope = receipt }), cancellation.Token));
        Assert.Equal(snapshot, session.SnapshotId);
    }

    private GraphSession Load()
    {
        var session = new GraphSession(_fs);
        session.Load(_root, null, null);
        return session;
    }

    [Fact]
    public void TotalByteBudgetAndAliasValidation_FailBeforeReturningAReceipt()
    {
        using var session = Load();
        var handler = Handler(session);
        _fs.Reads = 0;
        Assert.True(Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = new[] { "A.cs", "./A.cs" } }).Result.IsError);
        Assert.Equal(0, _fs.Reads);
        var bytes = new byte[FileScopeObservation.MaximumFileBytes];
        _fs.OpenOverride = _ => new MemoryStream(bytes, writable: false);
        var tooLarge = Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId = "owner", filePaths = Enumerable.Range(0, 9).Select(i => $"File{i}.bin").ToArray() });
        Assert.True(tooLarge.Result.IsError);
        Assert.False(tooLarge.Json.TryGetProperty("receipt", out _));
    }

    [Fact]
    public void HostNativePathSpelling_RoundTripsWithoutChangingAUnixBackslashFilename()
    {
        using var session = Load();
        var path = OperatingSystem.IsWindows() ? Path.Combine("sub", "C.cs") : "sub\\C.cs";
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "// content");
        var handler = Handler(session);
        var receipt = Capture(handler, "owner", path);
        var verified = Call(handler, "lifeblood_file_scope", new { action = "verify", ownerId = "owner", receipt });
        Assert.False(verified.Result.IsError);
        Assert.Equal("matched", verified.Json.GetProperty("status").GetString());
    }

    [Fact]
    public void WrongOwnerAndHistoricalSelection_DoNotObserveLiveFiles()
    {
        using var session = Load();
        var handler = Handler(session);
        var receipt = Capture(handler, "owner-a", "A.cs");
        _fs.Reads = 0;
        Assert.True(Call(handler, "lifeblood_file_scope", new { action = "verify", ownerId = "owner-b", receipt }).Result.IsError);
        Assert.Equal(0, _fs.Reads);
        var oldId = session.SnapshotId.ToString();
        File.WriteAllText(Path.Combine(_root, "B.cs"), "public class Other { }");
        session.Load(_root, null, null);
        _fs.Reads = 0;
        var historical = handler.Handle("lifeblood_file_scope", JsonSerializer.SerializeToElement(new
            { action = "capture", ownerId = "owner-a", filePaths = new[] { "A.cs" }, snapshotId = oldId }));
        Assert.True(historical.IsError);
        Assert.Contains("workspace", historical.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _fs.Reads);
    }

    [Fact]
    public void GuardedCheck_StrictJsonContractAcceptsBothKindsOfPrecondition()
    {
        using var session = Load();
        var handler = Handler(session, mode: ToolJsonCompatibilityMode.Strict);
        var receipt = Capture(handler, "owner", "A.cs");
        var check = Call(handler, "lifeblood_compile_check", new { filePath = "A.cs", expectedFileScope = receipt,
            expectedSnapshotId = session.SnapshotId.ToString(), expectedAnalysisGeneration = session.AnalysisGeneration });
        Assert.NotEqual(true, check.Result.IsError);
        Assert.True(check.Json.GetProperty("success").GetBoolean());
        Assert.True(check.Json.GetProperty("preconditions").GetProperty("publicationChecked").GetBoolean());
    }

    private ToolHandler Handler(GraphSession session, ISessionGate? gate = null, ToolJsonCompatibilityMode mode = ToolJsonCompatibilityMode.Legacy)
        => new(session, new LifebloodMcpProvider(new BlastRadius()), new LifebloodSymbolResolver(),
            new LifebloodSemanticSearchProvider(), new LifebloodDeadCodeAnalyzer(), new LifebloodPartialViewBuilder(_fs),
            new LifebloodInvariantProvider(_fs), new LifebloodResponseDecorator(ToolRegistry.GetDefinitions()
                .ToDictionary(definition => definition.Name, definition => definition.EnvelopeClassification!)), sessionGate: gate, jsonCompatibilityMode: mode);

    private static JsonElement Capture(ToolHandler handler, string ownerId, params string[] paths)
    {
        var result = Call(handler, "lifeblood_file_scope", new { action = "capture", ownerId, filePaths = paths });
        Assert.False(result.Result.IsError, result.Result.Content[0].Text);
        return result.Json.GetProperty("receipt").Clone();
    }

    private static (McpToolResult Result, JsonElement Json) Call(ToolHandler handler, string tool, object arguments)
    {
        var result = handler.Handle(tool, JsonSerializer.SerializeToElement(arguments));
        return (result, JsonSerializer.Deserialize<JsonElement>(result.Content[0].Text));
    }

    private sealed class BlastRadius : IBlastRadiusProvider
    {
        public BlastRadiusResult Analyze(SemanticGraph graph, string targetSymbolId, int maxDepth = 10)
            => BlastRadiusAnalyzer.Analyze(graph, targetSymbolId, maxDepth);
    }

    private sealed class HookFileSystem : IFileSystem
    {
        private readonly PhysicalFileSystem _physical = new();
        public Action<string>? AfterOpen;
        public Exception? OpenError;
        public Func<string, Stream>? OpenOverride;
        public int Reads;
        public int Writes;
        public string ReadAllText(string path) { Reads++; return _physical.ReadAllText(path); }
        public IEnumerable<string> ReadLines(string path) => _physical.ReadLines(path);
        public Stream OpenRead(string path)
        {
            Reads++;
            if (OpenError != null) throw OpenError;
            if (OpenOverride != null) return OpenOverride(path);
            var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            AfterOpen?.Invoke(path);
            return stream;
        }
        public Stream OpenWrite(string path) { Writes++; return _physical.OpenWrite(path); }
        public bool FileExists(string path) => _physical.FileExists(path);
        public bool DirectoryExists(string path) => _physical.DirectoryExists(path);
        public string[] FindFiles(string directory, string pattern, bool recursive = true) => _physical.FindFiles(directory, pattern, recursive);
        public DateTime GetLastWriteTimeUtc(string path) => _physical.GetLastWriteTimeUtc(path);
    }
}
