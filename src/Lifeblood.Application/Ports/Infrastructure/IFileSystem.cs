namespace Lifeblood.Application.Ports.Infrastructure;

public interface IFileSystem
{
    string ReadAllText(string path);
    IEnumerable<string> ReadLines(string path);
    Stream OpenRead(string path);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string[] FindFiles(string directory, string pattern, bool recursive = true);
    DateTime GetLastWriteTimeUtc(string path);
}
