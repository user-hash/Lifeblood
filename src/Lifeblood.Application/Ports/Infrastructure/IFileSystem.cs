namespace Lifeblood.Application.Ports.Infrastructure;

public interface IFileSystem
{
    string ReadAllText(string path);
    IEnumerable<string> ReadLines(string path);
    Stream OpenRead(string path);

    /// <summary>
    /// Opens a write stream at <paramref name="path"/>, creating the file
    /// if it does not exist and truncating it if it does. Companion to
    /// <see cref="OpenRead"/> so write-side consumers (graph export,
    /// generated context packs, etc.) route through the same port instead
    /// of reaching for <c>System.IO.File</c> directly. Caller owns the
    /// returned stream and is responsible for disposing it.
    /// </summary>
    Stream OpenWrite(string path);

    bool FileExists(string path);
    bool DirectoryExists(string path);
    string[] FindFiles(string directory, string pattern, bool recursive = true);
    DateTime GetLastWriteTimeUtc(string path);
}
