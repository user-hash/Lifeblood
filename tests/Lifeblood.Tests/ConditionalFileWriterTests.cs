using System.Text;
using System.Text.Json;
using Lifeblood.Adapters.CSharp;
using Lifeblood.Application.UseCases;
using Lifeblood.CLI;
using Xunit;

namespace Lifeblood.Tests;

public sealed class ConditionalFileWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lifeblood-conditional-write-" + Guid.NewGuid().ToString("N"));
    private readonly FileScopeObservation observer = new(new PhysicalFileSystem());
    private const string Target = "a-target.txt";
    private const string Guard = "z-guard.txt";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConditionalFileWriterTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Target), "original target with trailing bytes");
        File.WriteAllText(Path.Combine(root, Guard), "guard");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
    private FileScopeReceipt Capture() => observer.Capture(root, "owner-a", [Target, Guard]).Receipt;
    private void RequireWindows() => Skip.IfNot(OperatingSystem.IsWindows(), "Requires Windows mandatory file-sharing semantics.");

    [SkippableFact]
    public void Apply_PreservesRawBomBytesTruncatesTargetAndReturnsUsableReceipt()
    {
        RequireWindows();
        var expected = Capture();
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("new\r\n")).ToArray();
        var result = new ConditionalFileWriter().Apply(root, Target, expected, bytes);
        Assert.True(result.Success);
        Assert.Equal("applied", result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, Target)));
        Assert.Empty(FileScopeObservation.Compare(result.Receipt, Capture()));
        Assert.Equal("guard", File.ReadAllText(Path.Combine(root, Guard)));
    }

    [SkippableFact]
    public void ChangedGuardWithPreservedTimestamp_RejectsWithoutWritingTarget()
    {
        RequireWindows();
        var expected = Capture();
        var path = Path.Combine(root, Guard);
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "other owner");
        File.SetLastWriteTimeUtc(path, time);
        var result = new ConditionalFileWriter().Apply(root, Target, expected, "new"u8.ToArray());
        Assert.False(result.Success);
        Assert.Equal("conflict", result.Status);
        Assert.Equal(Guard, Assert.Single(result.Conflicts).Expected.FilePath);
        Assert.Equal("original target with trailing bytes", File.ReadAllText(Path.Combine(root, Target)));
    }

    [SkippableFact]
    public void DeletedTarget_ReportsConflictWithoutRecreatingIt()
    {
        RequireWindows();
        var expected = Capture();
        File.Delete(Path.Combine(root, Target));
        var result = new ConditionalFileWriter().Apply(root, Target, expected, "new"u8.ToArray());
        Assert.False(result.Success);
        Assert.Null(Assert.Single(result.Conflicts).Actual.Sha256);
        Assert.False(File.Exists(Path.Combine(root, Target)));
    }

    [SkippableFact]
    public void BusyGuard_FailsBeforeWriteAndReleasesAcquiredTargetHandle()
    {
        RequireWindows();
        var expected = Capture();
        using var busy = File.Open(Path.Combine(root, Guard), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => new ConditionalFileWriter().Apply(root, Target, expected, "new"u8.ToArray()));
        Assert.Equal("original target with trailing bytes", File.ReadAllText(Path.Combine(root, Target)));
        using var target = File.Open(Path.Combine(root, Target), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [SkippableFact]
    public async Task SameReceiptConcurrentWriters_ExactlyOneApplies()
    {
        RequireWindows();
        var expected = Capture();
        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            start.Wait();
            try
            {
                var result = new ConditionalFileWriter().Apply(root, Target, expected, Encoding.UTF8.GetBytes($"writer-{index}"));
                return (Index: index, Applied: result.Success);
            }
            catch (IOException) { return (Index: index, Applied: false); }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results, result => result.Applied);
        Assert.Equal($"writer-{winner.Index}", File.ReadAllText(Path.Combine(root, Target)));
    }

    [SkippableFact]
    public void DuringCommit_OrdinaryWritesAndAtomicRenameCannotBypassScopeLocks()
    {
        RequireWindows();
        var expected = Capture();
        var checkedLocks = false;
        var writer = new ConditionalFileWriter((path, access) =>
        {
            var stream = new FileStream(path, FileMode.Open, access, FileShare.None);
            return access == FileAccess.ReadWrite ? new ProbeStream(stream, beforeWrite: () =>
            {
                Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(root, Target), "racing writer"));
                Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(root, Guard), "racing guard"));
                Assert.Throws<IOException>(() => File.Move(Path.Combine(root, Target), Path.Combine(root, "renamed.txt")));
                checkedLocks = true;
            }) : stream;
        });
        Assert.True(writer.Apply(root, Target, expected, "new"u8.ToArray()).Success);
        Assert.True(checkedLocks);
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, Target)));
    }

    [SkippableFact]
    public void PartialWriteFailure_RestoresOriginalBytesBeforeReleasingLocks()
    {
        RequireWindows();
        var expected = Capture();
        var calls = 0;
        var writer = new ConditionalFileWriter((path, access) =>
        {
            var stream = new FileStream(path, FileMode.Open, access, FileShare.None);
            return access == FileAccess.ReadWrite ? new ProbeStream(stream, beforeWrite: () =>
            {
                if (++calls != 1) return;
                stream.WriteByte(0);
                throw new IOException("simulated partial write");
            }) : stream;
        });
        var error = Assert.Throws<ConditionalFileWriteException>(() => writer.Apply(root, Target, expected, "new"u8.ToArray()));
        Assert.True(error.OriginalContentRestored);
        Assert.Empty(FileScopeObservation.Compare(expected, Capture()));
    }

    [SkippableFact]
    public void FailedRestoration_IsReportedAsUnknownContent()
    {
        RequireWindows();
        var expected = Capture();
        var writer = new ConditionalFileWriter((path, access) =>
        {
            var stream = new FileStream(path, FileMode.Open, access, FileShare.None);
            return access == FileAccess.ReadWrite ? new ProbeStream(stream, beforeWrite: () =>
            {
                stream.WriteByte(0);
                throw new IOException("simulated persistent storage failure");
            }) : stream;
        });
        var error = Assert.Throws<ConditionalFileWriteException>(() => writer.Apply(root, Target, expected, "new"u8.ToArray()));
        Assert.False(error.OriginalContentRestored);
        Assert.IsType<AggregateException>(error.InnerException);
        Assert.NotEmpty(FileScopeObservation.Compare(expected, Capture()));
    }

    [SkippableFact]
    public void CancellationBeforeCommit_ReleasesLocksWithoutWriting()
    {
        RequireWindows();
        var expected = Capture();
        using var canceled = new CancellationTokenSource();
        var writer = new ConditionalFileWriter((path, access) =>
        {
            var stream = new FileStream(path, FileMode.Open, access, FileShare.None);
            if (access == FileAccess.Read) canceled.Cancel();
            return stream;
        });
        Assert.Throws<OperationCanceledException>(() => writer.Apply(root, Target, expected, "new"u8.ToArray(), canceled.Token));
        Assert.Empty(FileScopeObservation.Compare(expected, Capture()));
    }

    [SkippableFact]
    public void IdenticalReplacement_DoesNotTouchTimestamp()
    {
        RequireWindows();
        var expected = Capture();
        var path = Path.Combine(root, Target);
        var time = File.GetLastWriteTimeUtc(path);
        var result = new ConditionalFileWriter().Apply(root, Target, expected, File.ReadAllBytes(path));
        Assert.Equal("unchanged", result.Status);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
    }

    [SkippableFact]
    public void AbsentGuardsUncoveredTargetsAndOversizedReplacements_RejectWithoutWrites()
    {
        RequireWindows();
        var expected = Capture();
        var absent = expected with { Files = [.. expected.Files, new FileContentHash("absent.txt", null, null)] };
        var writer = new ConditionalFileWriter();
        Assert.Throws<ArgumentException>(() => writer.Apply(root, Target, absent, "new"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => writer.Apply(root, "uncovered.txt", expected, "new"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => writer.Apply(root, Target, expected, new byte[FileScopeObservation.MaximumFileBytes + 1]));
        Assert.Empty(FileScopeObservation.Compare(expected, Capture()));
    }

    [SkippableFact]
    public void UnrelatedOwnerChange_DoesNotInvalidateScopedWrite()
    {
        RequireWindows();
        var expected = Capture();
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "other owner");
        Assert.True(new ConditionalFileWriter().Apply(root, Target, expected, "new"u8.ToArray()).Success);
        Assert.Equal("other owner", File.ReadAllText(Path.Combine(root, "unrelated.txt")));
    }

    [SkippableFact]
    public void Cli_RoundTripsNativeReceiptAndRejectsReusingStaleReceipt()
    {
        RequireWindows();
        var receiptPath = Path.Combine(root, "receipt.json");
        var stagedPath = Path.Combine(root, "staged.txt");
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(Capture(), Json));
        File.WriteAllText(stagedPath, "new");
        string[] args = ["--project", root, "--target", Target, "--receipt", receiptPath, "--replacement", stagedPath];
        using var output = new StringWriter();
        Assert.Equal(0, ApplyFileCommand.Run(args, output));
        using var applied = JsonDocument.Parse(output.ToString());
        Assert.Equal("applied", applied.RootElement.GetProperty("status").GetString());
        var receipt = applied.RootElement.GetProperty("receipt").Deserialize<FileScopeReceipt>(Json)!;
        Assert.Empty(FileScopeObservation.Compare(receipt, Capture()));
        output.GetStringBuilder().Clear();
        Assert.Equal(2, ApplyFileCommand.Run(args, output));
        using var rejected = JsonDocument.Parse(output.ToString());
        Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, Target)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("nullFile")]
    [InlineData("nullRoot")]
    public void Cli_MalformedReceiptsFailClosed(string mutation)
    {
        var receipt = JsonSerializer.Serialize(Capture(), Json);
        receipt = mutation switch
        {
            "unknown" => receipt.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true"),
            "duplicate" => receipt.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "missing" => receipt.Replace("\"schemaVersion\":1,", ""),
            "nullFile" => receipt.Replace("\"files\":[", "\"files\":[null,"),
            "nullRoot" => receipt.Replace(JsonSerializer.Serialize(root), "null"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var receiptPath = Path.Combine(root, "receipt.json");
        File.WriteAllText(receiptPath, receipt);
        using var output = new StringWriter();
        Assert.Equal(1, ApplyFileCommand.Run(["--project", root, "--target", Target, "--receipt", receiptPath, "--replacement", "unused"], output));
        using var error = JsonDocument.Parse(output.ToString());
        Assert.Equal("file-apply-contract", error.RootElement.GetProperty("failure").GetString());
        Assert.Equal("original target with trailing bytes", File.ReadAllText(Path.Combine(root, Target)));
    }

    [Fact]
    public void Cli_DuplicateOptionsAreRejected()
    {
        using var output = new StringWriter();
        Assert.Equal(1, ApplyFileCommand.Run(["--project", root, "--target", Target, "--receipt", "a", "--receipt", "b"], output));
        Assert.Contains("file-apply-contract", output.ToString());
    }

    private sealed class ProbeStream(Stream inner, Action beforeWrite) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { beforeWrite(); inner.Write(buffer, offset, count); }
        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
