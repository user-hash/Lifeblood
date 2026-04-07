namespace Lifeblood.Application.Ports.Infrastructure;

public interface IFileSystem
{
    string ReadAllText(string path);
    bool FileExists(string path);
    string[] FindFiles(string directory, string pattern, bool recursive = true);
}
