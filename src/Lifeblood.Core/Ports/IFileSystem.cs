namespace Lifeblood.Core.Ports;

/// <summary>
/// Abstracts file system access. Enables testing with in-memory file systems.
/// Adapters use this to read source files instead of System.IO directly.
/// </summary>
public interface IFileSystem
{
    /// <summary>Read all text from a file.</summary>
    string ReadAllText(string path);

    /// <summary>Check if a file exists.</summary>
    bool FileExists(string path);

    /// <summary>Find files matching a glob pattern under a directory.</summary>
    string[] FindFiles(string directory, string pattern, bool recursive = true);

    /// <summary>Get all files under a directory.</summary>
    string[] GetFiles(string directory, string extension, bool recursive = true);
}
