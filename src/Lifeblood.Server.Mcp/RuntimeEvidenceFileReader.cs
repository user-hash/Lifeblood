using System.Text;
using Lifeblood.Application.Ports.Infrastructure;
using Lifeblood.Domain.Results;

namespace Lifeblood.Server.Mcp;

/// <summary>One workspace containment and bounded UTF-8 read policy for runtime-evidence consumers.</summary>
internal static class RuntimeEvidenceFileReader
{
    internal const int MaximumInputBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PerformanceEvidenceDocument Read(
        IFileSystem fileSystem, string workspaceRoot, string rawPath, string? format, int maximumMeasurements)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) throw new ArgumentException("Runtime-evidence path cannot be empty.", nameof(rawPath));
        var root = Path.GetFullPath(workspaceRoot);
        var path = WorkspacePathIdentity.ResolveFromWorkspace(root, rawPath);
        if (!WorkspacePathIdentity.Contains(root, path)) throw new ArgumentException($"Runtime-evidence path must stay inside workspace root '{root}'.");
        if (!fileSystem.FileExists(path)) throw new FileNotFoundException("Runtime-evidence capture was not found.", path);
        using var source = fileSystem.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = source.Read(chunk, 0, chunk.Length);
            if (read == 0) break;
            if (buffer.Length + read > MaximumInputBytes) throw new ArgumentException($"Runtime-evidence capture exceeds the {MaximumInputBytes}-byte limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return new PerformanceEvidenceDocument(Path.GetRelativePath(root, path).Replace('\\', '/'),
                StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)), format, maximumMeasurements);
        }
        catch (DecoderFallbackException error)
        {
            throw new ArgumentException("Runtime-evidence capture must be valid UTF-8.", error);
        }
    }
}
