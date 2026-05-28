using System.Text.Json;
using System.Text.Json.Serialization;
using Lifeblood.Server.Mcp;
using Xunit;

namespace Lifeblood.Tests;

/// <summary>
/// INV-WIRE-CONTRACT-001: every MCP tool input schema exposed through
/// tools/list is pinned to a versioned snapshot under schemas/tools/v1.
/// </summary>
public class ToolSchemaSnapshotTests
{
    private const string SnapshotDirectoryName = "schemas/tools/v1";
    private const string SnapshotSuffix = ".schema.json";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [Fact]
    public void ToolRegistry_EveryToolInputSchema_MatchesVersionedSnapshot()
    {
        var definitions = ToolRegistry.GetDefinitions()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(definitions);

        foreach (var definition in definitions)
        {
            var snapshotPath = Path.Combine(SnapshotDirectory, definition.Name + SnapshotSuffix);
            Assert.True(
                File.Exists(snapshotPath),
                $"{definition.Name} is missing its v1 input-schema snapshot at {snapshotPath}.");

            var expected = Canonicalize(File.ReadAllText(snapshotPath));
            var actual = Canonicalize(JsonSerializer.Serialize(definition.InputSchema, SnapshotJsonOptions));

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ToolSchemaSnapshotDirectory_HasNoUnknownToolFiles()
    {
        var knownToolNames = ToolRegistry.GetDefinitions()
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);

        var snapshotFiles = Directory.EnumerateFiles(SnapshotDirectory, "*" + SnapshotSuffix)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(snapshotFiles);

        foreach (var file in snapshotFiles)
        {
            var fileName = Path.GetFileName(file);
            Assert.EndsWith(SnapshotSuffix, fileName, StringComparison.Ordinal);

            var toolName = fileName[..^SnapshotSuffix.Length];
            Assert.Contains(toolName, knownToolNames);
        }
    }

    private static string SnapshotDirectory =>
        Path.Combine(RepoRoot, SnapshotDirectoryName.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "Lifeblood.sln")))
            {
                current = current.Parent;
            }

            Assert.NotNull(current);
            return current!.FullName;
        }
    }

    private static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return JsonSerializer.Serialize(document.RootElement, SnapshotJsonOptions);
    }
}
