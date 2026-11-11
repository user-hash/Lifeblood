using System.Security.Cryptography;
using Lifeblood.Application.UseCases;

namespace Lifeblood.Adapters.CSharp;

public sealed record ConditionalFileWriteResult(
    bool Success, string Status, FileScopeReceipt Receipt, IReadOnlyList<FileScopeConflict> Conflicts);

public sealed class ConditionalFileWriteException(Exception writeFailure, Exception? restoreFailure)
    : IOException(restoreFailure == null
        ? "Conditional write failed; the original bytes were restored."
        : "Conditional write and restoration failed; inspect the target before retrying.",
        restoreFailure == null ? writeFailure : new AggregateException(writeFailure, restoreFailure))
{
    public bool OriginalContentRestored => restoreFailure == null;
}

/// <summary>Single existing-file conditional writes under Windows sharing locks.</summary>
public sealed class ConditionalFileWriter
{
    private readonly Func<string, FileAccess, Stream> open;

    public ConditionalFileWriter() : this((path, access) =>
        new FileStream(path, FileMode.Open, access, FileShare.None)) { }

    internal ConditionalFileWriter(Func<string, FileAccess, Stream> open) => this.open = open;

    public ConditionalFileWriteResult Apply(
        string workspaceRoot, string targetPath, FileScopeReceipt expected,
        ReadOnlyMemory<byte> replacement, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("apply-file requires Windows sharing-lock semantics.");
        FileScopeObservation.ValidateReceipt(expected, workspaceRoot);
        var root = FileScopeObservation.NormalizeRoot(workspaceRoot);
        var target = FileScopeObservation.ResolvePath(root, targetPath);
        if (!expected.Files.Any(file => FileScopeObservation.PathComparer.Equals(
                target, FileScopeObservation.ResolvePath(root, file.FilePath))))
            throw new ArgumentException("The target must be covered by the expected receipt.");
        if (expected.Files.Any(file => file.Sha256 == null))
            throw new ArgumentException("apply-file requires existing-file expectations for every guard; absent paths cannot be locked.");
        if (replacement.Length > FileScopeObservation.MaximumFileBytes)
            throw new ArgumentException("Replacement exceeds 8 MiB.");

        // Own the staged bytes before entering the protected operation.
        var staged = replacement.ToArray();
        var handles = new Dictionary<string, Stream>(FileScopeObservation.PathComparer);
        try
        {
            foreach (var path in expected.Files.Select(file => FileScopeObservation.ResolvePath(root, file.FilePath))
                         .OrderBy(path => path, FileScopeObservation.PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { handles.Add(path, open(path, FileScopeObservation.PathComparer.Equals(path, target) ? FileAccess.ReadWrite : FileAccess.Read)); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // A disappeared guard is a content conflict, never permission to create it.
                }
            }

            var observed = new List<FileContentHash>();
            byte[]? original = null;
            long totalBytes = 0;
            foreach (var file in expected.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = FileScopeObservation.ResolvePath(root, file.FilePath);
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!handles.TryGetValue(path, out var handle))
                {
                    observed.Add(new FileContentHash(relative, null, null));
                    continue;
                }
                var bytes = ReadBounded(handle, cancellationToken);
                totalBytes += bytes.Length;
                if (totalBytes > FileScopeObservation.MaximumTotalBytes)
                    throw new ArgumentException("File scope exceeds 64 MiB total.");
                observed.Add(Hash(relative, bytes));
                if (FileScopeObservation.PathComparer.Equals(path, target)) original = bytes;
            }
            var actual = new FileScopeReceipt(1, expected.OwnerId, root, observed);
            var conflicts = FileScopeObservation.Compare(expected, actual);
            if (conflicts.Length != 0) return new(false, "conflict", actual, conflicts);
            if (totalBytes - original!.Length + staged.Length > FileScopeObservation.MaximumTotalBytes)
                throw new ArgumentException("Updated file scope would exceed 64 MiB total.");

            cancellationToken.ThrowIfCancellationRequested();
            var targetStream = handles[target];
            if (original.AsSpan().SequenceEqual(staged)) return new(true, "unchanged", actual, []);
            try
            {
                // Cancellation is honored before commit, never halfway through a write.
                ReplaceBytes(targetStream, staged);
            }
            catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
            {
                Exception? restoreFailure = null;
                try { ReplaceBytes(targetStream, original); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { restoreFailure = failure; }
                throw new ConditionalFileWriteException(writeFailure, restoreFailure);
            }
            var updated = actual with { Files = observed.Select(file => FileScopeObservation.PathComparer.Equals(
                FileScopeObservation.ResolvePath(root, file.FilePath), target) ? Hash(file.FilePath, staged) : file).ToArray() };
            return new(true, "applied", updated, []);
        }
        finally
        {
            foreach (var handle in handles.Values) handle.Dispose();
        }
    }

    public static byte[] ReadReplacement(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return ReadBounded(stream, cancellationToken);
    }

    private static byte[] ReadBounded(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + read > FileScopeObservation.MaximumFileBytes)
                throw new ArgumentException("File exceeds 8 MiB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static FileContentHash Hash(string path, byte[] bytes) =>
        new(path, "sha256_" + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    private static void ReplaceBytes(Stream stream, byte[] bytes)
    {
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        if (stream is FileStream file) file.Flush(flushToDisk: true);
        else stream.Flush();
    }
}
