using System.Security.Cryptography;
using System.Text;
using Lifeblood.Application.Ports.Infrastructure;

namespace Lifeblood.Application.UseCases;

public sealed record FileContentHash(string FilePath, string? Sha256, long? ByteLength);

/// <summary>Caller-owned content expectations, independent of semantic publication identity.</summary>
public sealed record FileScopeReceipt(
    int SchemaVersion,
    string OwnerId,
    string WorkspaceRoot,
    IReadOnlyList<FileContentHash> Files);

public sealed record FileScopeConflict(FileContentHash Expected, FileContentHash Actual);

/// <summary>Request-local bytes; never stored in the semantic publication or a server receipt cache.</summary>
public sealed record CapturedFileScope(
    FileScopeReceipt Receipt,
    IReadOnlyDictionary<string, byte[]> Content)
{
    public string ReadText(string fullPath)
    {
        using var stream = new MemoryStream(Content[fullPath], writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>Bounded raw-byte observations of an explicit file set through the filesystem port.</summary>
public sealed class FileScopeObservation(IFileSystem fileSystem)
{
    public const int MaximumFiles = 32;
    public const long MaximumFileBytes = 8 * 1024 * 1024;
    public const long MaximumTotalBytes = 64 * 1024 * 1024;
    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public static string ResolvePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File paths cannot be empty.");
        var fullPath = Path.GetFullPath(path, NormalizeRoot(root));
        var relative = Path.GetRelativePath(NormalizeRoot(root), fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"File path '{path}' must be inside the loaded workspace.");
        return fullPath;
    }

    public static void ValidateReceipt(FileScopeReceipt receipt, string workspaceRoot)
    {
        if (receipt.SchemaVersion != 1) throw new ArgumentException("Unsupported file-scope receipt schemaVersion.");
        if (string.IsNullOrWhiteSpace(receipt.OwnerId) || receipt.OwnerId.Length > 128)
            throw new ArgumentException("ownerId must contain 1..128 non-whitespace characters.");
        if (!PathComparer.Equals(NormalizeRoot(receipt.WorkspaceRoot), NormalizeRoot(workspaceRoot)))
            throw new ArgumentException("File-scope receipt belongs to a different workspace.");
        _ = ResolvePaths(workspaceRoot, receipt.Files.Select(file => file.FilePath).ToArray());
        foreach (var file in receipt.Files)
        {
            if (file.Sha256 == null && file.ByteLength == null) continue;
            if (file.ByteLength is < 0 or > MaximumFileBytes || file.ByteLength == null
                || file.Sha256 is not { Length: 71 } hash || !hash.StartsWith("sha256_", StringComparison.Ordinal)
                || hash.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
                throw new ArgumentException($"Invalid content expectation for '{file.FilePath}'. Missing files require null sha256 and byteLength.");
        }
        if (receipt.Files.Sum(file => file.ByteLength ?? 0) > MaximumTotalBytes)
            throw new ArgumentException("File-scope receipt exceeds the total byte limit.");
    }

    public CapturedFileScope Capture(
        string workspaceRoot, string ownerId, IReadOnlyList<string> paths,
        bool includeContent = false, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(workspaceRoot);
        var resolved = ResolvePaths(root, paths);
        ValidateReceipt(new FileScopeReceipt(1, ownerId, root,
            resolved.Select(path => new FileContentHash(path, null, null)).ToArray()), root);
        var hashes = new List<FileContentHash>(resolved.Length);
        var content = new Dictionary<string, byte[]>(PathComparer);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        foreach (var path in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                using var stream = fileSystem.OpenRead(path);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var bytes = includeContent ? new MemoryStream() : null;
                long length = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    length += read;
                    totalBytes += read;
                    if (length > MaximumFileBytes || totalBytes > MaximumTotalBytes)
                        throw new ArgumentException("File scope exceeds 8 MiB per file or 64 MiB total.");
                    hash.AppendData(buffer, 0, read);
                    bytes?.Write(buffer, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                hashes.Add(new FileContentHash(relative, "sha256_" + Convert.ToHexStringLower(hash.GetHashAndReset()), length));
                if (bytes != null) content.Add(path, bytes.ToArray());
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                hashes.Add(new FileContentHash(relative, null, null));
            }
        }
        return new CapturedFileScope(new FileScopeReceipt(1, ownerId, root, hashes), content);
    }

    public static FileScopeConflict[] Compare(FileScopeReceipt expected, FileScopeReceipt actual)
    {
        ValidateReceipt(expected, actual.WorkspaceRoot);
        if (!string.Equals(expected.OwnerId, actual.OwnerId, StringComparison.Ordinal))
            throw new ArgumentException("Expected and observed scope owner labels differ.");
        var expectedByPath = expected.Files.ToDictionary(file => ResolvePath(actual.WorkspaceRoot, file.FilePath), PathComparer);
        var actualByPath = actual.Files.ToDictionary(file => ResolvePath(actual.WorkspaceRoot, file.FilePath), PathComparer);
        if (expectedByPath.Count != actualByPath.Count || expectedByPath.Keys.Any(path => !actualByPath.ContainsKey(path)))
            throw new ArgumentException("Expected and observed file scopes differ.");
        return expectedByPath.Where(pair => pair.Value.Sha256 != actualByPath[pair.Key].Sha256
                || pair.Value.ByteLength != actualByPath[pair.Key].ByteLength)
            .Select(pair => new FileScopeConflict(pair.Value, actualByPath[pair.Key])).ToArray();
    }

    private static string[] ResolvePaths(string root, IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > MaximumFiles)
            throw new ArgumentException($"File scope requires 1..{MaximumFiles} paths.");
        var resolved = paths.Select(path => ResolvePath(root, path)).ToArray();
        if (resolved.Distinct(PathComparer).Count() != resolved.Length)
            throw new ArgumentException("File scope contains duplicate resolved paths.");
        return resolved;
    }
}
