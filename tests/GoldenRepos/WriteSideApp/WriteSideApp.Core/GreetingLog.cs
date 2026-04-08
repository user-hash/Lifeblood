namespace WriteSideApp.Core;

/// <summary>
/// Collection type. Demonstrates: indexer property, generic type usage.
/// </summary>
public class GreetingLog
{
    private readonly List<string> _entries = new();

    /// <summary>Indexer — tests IsIndexer extraction.</summary>
    public string this[int index] => _entries[index];

    public int Count => _entries.Count;

    public void Add(string entry) => _entries.Add(entry);
}
