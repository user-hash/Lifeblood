using System.Text.Json;
using Lifeblood.Application.UseCases;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

internal sealed record SnapshotReadBinding(
    WorkspaceSnapshotPrecondition? Precondition,
    string? Error)
{
    public bool Accepted => Error == null;
}

internal static class SnapshotReadRequestBinder
{
    public static SnapshotReadBinding Bind(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return new SnapshotReadBinding(null, null);

        SnapshotId? expectedSnapshotId = null;
        long? expectedGeneration = null;
        if (value.TryGetProperty("expectedSnapshotId", out var snapshotElement))
        {
            if (snapshotElement.ValueKind != JsonValueKind.String
                || !SnapshotId.TryParse(snapshotElement.GetString(), out expectedSnapshotId))
            {
                return new SnapshotReadBinding(
                    null,
                    "expectedSnapshotId must use the canonical snap_<32 lowercase hex digits> form.");
            }
        }

        if (value.TryGetProperty("expectedAnalysisGeneration", out var generationElement))
        {
            if (generationElement.ValueKind != JsonValueKind.Number
                || !generationElement.TryGetInt64(out var parsedGeneration)
                || parsedGeneration < 0)
            {
                return new SnapshotReadBinding(
                    null,
                    "expectedAnalysisGeneration must be a non-negative 64-bit integer.");
            }

            expectedGeneration = parsedGeneration;
        }

        var precondition = expectedSnapshotId == null && expectedGeneration == null
            ? null
            : new WorkspaceSnapshotPrecondition(expectedSnapshotId, expectedGeneration);
        return new SnapshotReadBinding(precondition, null);
    }
}

internal sealed record ToolBatchCall(string ToolName, JsonElement? Arguments);

internal sealed record ToolBatchRequest(IReadOnlyList<ToolBatchCall> Calls);

internal sealed class ToolBatchContractException : ArgumentException
{
    public ToolBatchContractException(string message)
        : base(message)
    {
    }
}

internal static class ToolBatchRequestBinder
{
    public const int MaximumCallCount = 32;

    public static ToolBatchRequest Bind(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("calls", out var callsElement)
            || callsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ToolBatchContractException("calls must be a non-empty array of read-only tool calls.");
        }

        var callCount = callsElement.GetArrayLength();
        if (callCount is < 1 or > MaximumCallCount)
        {
            throw new ToolBatchContractException(
                $"calls must contain between 1 and {MaximumCallCount} items.");
        }

        var calls = new List<ToolBatchCall>(callCount);
        var index = 0;
        foreach (var callElement in callsElement.EnumerateArray())
        {
            if (callElement.ValueKind != JsonValueKind.Object)
                throw new ToolBatchContractException($"calls[{index}] must be an object.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in callElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new ToolBatchContractException($"calls[{index}] contains duplicate '{property.Name}'.");
                if (property.Name is not ("tool" or "arguments"))
                    throw new ToolBatchContractException($"calls[{index}] contains unknown field '{property.Name}'.");
            }

            if (!callElement.TryGetProperty("tool", out var toolElement)
                || toolElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(toolElement.GetString()))
            {
                throw new ToolBatchContractException($"calls[{index}].tool must be a non-empty string.");
            }

            JsonElement? callArguments = null;
            if (callElement.TryGetProperty("arguments", out var argumentsElement))
            {
                if (argumentsElement.ValueKind == JsonValueKind.Object)
                    callArguments = argumentsElement.Clone();
                else if (argumentsElement.ValueKind != JsonValueKind.Null)
                    throw new ToolBatchContractException($"calls[{index}].arguments must be an object or null.");
            }

            calls.Add(new ToolBatchCall(toolElement.GetString()!, callArguments));
            index++;
        }

        return new ToolBatchRequest(calls);
    }
}
