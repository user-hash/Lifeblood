using System.Text.Json;
using Lifeblood.Application.UseCases;

namespace Lifeblood.Server.Mcp;

internal sealed class FileScopeToolHandler(GraphSession session, JsonSerializerOptions jsonOptions)
{
    internal const string Limitations = "Content hashes cover only the explicit files, not dependency/profile freshness. ownerId labels caller intent; it is not an exclusive lock or authorization. Files are observed individually, not as an atomic filesystem snapshot. No source files are written.";

    public McpToolResult Handle(JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args)
            return Error("file-scope-contract", "An object with action and ownerId is required.");
        try
        {
            var action = RequiredString(args, "action");
            var ownerId = RequiredString(args, "ownerId");
            FileScopeReceipt? expected = null;
            string[] paths;
            if (action == "capture")
            {
                if (args.TryGetProperty("receipt", out _)) throw new ArgumentException("capture accepts filePaths, not receipt.");
                paths = ReadPaths(args.GetProperty("filePaths"));
            }
            else if (action == "verify")
            {
                if (args.TryGetProperty("filePaths", out _)) throw new ArgumentException("verify uses exactly the receipt paths; omit filePaths.");
                expected = BindReceipt(args.GetProperty("receipt"), session.ProjectRoot);
                if (!string.Equals(expected.OwnerId, ownerId, StringComparison.Ordinal))
                    throw new ArgumentException("ownerId must match the receipt ownerId.");
                paths = expected.Files.Select(file => file.FilePath).ToArray();
            }
            else throw new ArgumentException("action must be capture or verify.");

            var captured = new FileScopeObservation(session.FileSystem).Capture(session.ProjectRoot, ownerId, paths,
                cancellationToken: cancellationToken);
            var conflicts = expected == null ? Array.Empty<FileScopeConflict>() : FileScopeObservation.Compare(expected, captured.Receipt);
            return Text(new
            {
                success = conflicts.Length == 0,
                action,
                status = expected == null ? "captured" : conflicts.Length == 0 ? "matched" : "conflict",
                checkedAtUtc = DateTime.UtcNow,
                receipt = captured.Receipt,
                conflicts,
                semanticInputsVerified = false,
                limitations = new[] { Limitations },
            }, conflicts.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Text(new { failure = "file-scope-unavailable", message = ex.Message, retryable = ex is IOException }, true);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return Error("file-scope-contract", ex.Message);
        }
    }

    internal static FileScopeReceipt BindReceipt(JsonElement value, string root)
    {
        RequireObject(value, "receipt", "schemaVersion", "ownerId", "workspaceRoot", "files");
        var version = value.GetProperty("schemaVersion");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
            throw new ArgumentException("receipt.schemaVersion must be 1.");
        var files = value.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > FileScopeObservation.MaximumFiles)
            throw new ArgumentException("receipt.files must contain 1..32 file expectations.");
        var entries = files.EnumerateArray().Select(file =>
        {
            RequireObject(file, "receipt.files[]", "filePath", "sha256", "byteLength");
            var hash = file.GetProperty("sha256");
            var length = file.GetProperty("byteLength");
            if (hash.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || length.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)
                || length.ValueKind == JsonValueKind.Number && !length.TryGetInt64(out _))
                throw new ArgumentException("Expected sha256:string|null and byteLength:integer|null.");
            return new FileContentHash(RequiredString(file, "filePath"), hash.GetString(),
                length.ValueKind == JsonValueKind.Null ? null : length.GetInt64());
        }).ToArray();
        var receipt = new FileScopeReceipt(schemaVersion, RequiredString(value, "ownerId"),
            RequiredString(value, "workspaceRoot"), entries);
        FileScopeObservation.ValidateReceipt(receipt, root);
        return receipt;
    }

    internal static string[] ReadPaths(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > FileScopeObservation.MaximumFiles
            || value.EnumerateArray().Any(path => path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString())))
            throw new ArgumentException("filePaths requires 1..32 non-empty strings.");
        return value.EnumerateArray().Select(path => path.GetString()!).ToArray();
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var element = value.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new ArgumentException($"{name} must be a non-empty string.");
        return element.GetString()!;
    }

    private static void RequireObject(JsonElement value, string name, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{name} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !fields.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException($"{name} has an unknown or duplicate field '{property.Name}'.");
        if (fields.Any(field => !seen.Contains(field))) throw new ArgumentException($"{name} is missing a required field.");
    }

    private McpToolResult Error(string failure, string message) => Text(new { failure, message }, true);
    private McpToolResult Text(object value, bool error = false) => new()
    {
        IsError = error,
        Content = new[] { new McpContent { Type = "text", Text = JsonSerializer.Serialize(value, jsonOptions) } },
    };
}
