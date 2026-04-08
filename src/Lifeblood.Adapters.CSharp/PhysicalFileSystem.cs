using Lifeblood.Application.Ports.Infrastructure;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Default IFileSystem backed by System.IO.
/// Lives in the adapter layer — both composition roots reference this project.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public string ReadAllText(string path) => File.ReadAllText(path);

    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string[] FindFiles(string directory, string pattern, bool recursive = true) =>
        Directory.GetFiles(directory, pattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
}
