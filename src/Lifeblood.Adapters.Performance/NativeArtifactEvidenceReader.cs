using System.Text.Json;
using Lifeblood.Domain.Results;

namespace Lifeblood.Adapters.Performance;

public sealed partial class RuntimePerformanceEvidenceImporter
{
    private static NativeArtifactEvidence[] ReadNativeArtifacts(JsonElement root)
    {
        if (!root.TryGetProperty("nativeArtifacts", out var artifacts)) return Array.Empty<NativeArtifactEvidence>();
        RejectDuplicateNativeProperties(root);
        if (root.TryGetProperty("identity", out var identity) && identity.ValueKind == JsonValueKind.Object)
            RejectDuplicateNativeProperties(identity);
        if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() > 32)
            throw new ArgumentException("nativeArtifacts must be an array of at most 32 artifacts.");
        var result = new List<NativeArtifactEvidence>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var documentCount = 0;
        foreach (var artifact in artifacts.EnumerateArray())
        {
            NativeShape(artifact, "artifactId", "buildId", "debugId", "debugFormat", "documents");
            var id = NativeString(artifact, "artifactId", 256);
            if (id.Length > 0 && !ids.Add(id)) throw new ArgumentException("nativeArtifacts artifactId values must be distinct.");
            var documents = new List<NativeSourceDocument>();
            if (artifact.TryGetProperty("documents", out var rows))
            {
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256 - documentCount)
                    throw new ArgumentException("nativeArtifacts documents must be arrays totaling at most 256 rows.");
                foreach (var row in rows.EnumerateArray())
                {
                    NativeShape(row, "workspaceFilePath", "sourcePath", "moduleName", "profileScope", "checksumKind", "checksumAlgorithm", "checksum");
                    documents.Add(new NativeSourceDocument
                    {
                        WorkspaceFilePath = NativeString(row, "workspaceFilePath", 1024),
                        SourcePath = NativeString(row, "sourcePath", 2048),
                        ModuleName = NativeString(row, "moduleName", 256),
                        ProfileScope = NativeString(row, "profileScope", 128),
                        ChecksumKind = NativeString(row, "checksumKind", 32),
                        ChecksumAlgorithm = NativeString(row, "checksumAlgorithm", 32),
                        Checksum = NativeString(row, "checksum", 128),
                    });
                    documentCount++;
                }
            }
            result.Add(new NativeArtifactEvidence
            {
                ArtifactId = id,
                BuildId = NativeString(artifact, "buildId", 256),
                DebugId = NativeString(artifact, "debugId", 512),
                DebugFormat = NativeString(artifact, "debugFormat", 64),
                Documents = documents.ToArray(),
            });
        }
        return result.ToArray();
    }

    private static void NativeShape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Native artifact/document evidence must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !fields.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"Duplicate or unknown native evidence field '{property.Name}'.");
    }

    private static string NativeString(JsonElement value, string name, int limit)
    {
        if (!value.TryGetProperty(name, out var property)) return "";
        if (property.ValueKind != JsonValueKind.String) throw new ArgumentException($"Native evidence '{name}' must be a string.");
        var text = property.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl)) throw new ArgumentException($"Native evidence '{name}' exceeds its text bounds.");
        return text;
    }

    private static void RejectDuplicateNativeProperties(JsonElement value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name)) throw new ArgumentException($"Duplicate native capture identity field '{property.Name}'.");
    }
}
