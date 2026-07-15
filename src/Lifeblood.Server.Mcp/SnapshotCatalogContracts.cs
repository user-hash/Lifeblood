using System.Text.Json;
using Lifeblood.Domain.Workspaces;

namespace Lifeblood.Server.Mcp;

internal enum SnapshotCatalogAction
{
    List,
    Pin,
    Unpin,
    Evict,
}

internal sealed record SnapshotCatalogRequest(
    SnapshotCatalogAction Action,
    SnapshotId? TargetSnapshotId,
    string? Name,
    bool CheckDrift);

internal sealed class SnapshotCatalogContractException : ArgumentException
{
    public SnapshotCatalogContractException(string message)
        : base(message)
    {
    }
}

internal static class SnapshotCatalogRequestBinder
{
    public static SnapshotCatalogRequest Bind(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value)
            return new SnapshotCatalogRequest(SnapshotCatalogAction.List, null, null, CheckDrift: false);

        var action = SnapshotCatalogAction.List;
        if (value.TryGetProperty("action", out var actionElement))
        {
            if (actionElement.ValueKind != JsonValueKind.String)
                throw new SnapshotCatalogContractException("action must be one of list, pin, unpin, or evict.");
            action = actionElement.GetString() switch
            {
                "list" => SnapshotCatalogAction.List,
                "pin" => SnapshotCatalogAction.Pin,
                "unpin" => SnapshotCatalogAction.Unpin,
                "evict" => SnapshotCatalogAction.Evict,
                _ => throw new SnapshotCatalogContractException("action must be one of list, pin, unpin, or evict."),
            };
        }

        SnapshotId? target = null;
        if (value.TryGetProperty("targetSnapshotId", out var targetElement))
        {
            if (targetElement.ValueKind != JsonValueKind.String
                || !SnapshotId.TryParse(targetElement.GetString(), out target))
            {
                throw new SnapshotCatalogContractException(
                    "targetSnapshotId must use the canonical snap_<32 lowercase hex digits> form.");
            }
        }

        string? name = null;
        if (value.TryGetProperty("name", out var nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.String)
                throw new SnapshotCatalogContractException("name must be a string.");
            name = nameElement.GetString();
        }

        var checkDrift = false;
        if (value.TryGetProperty("checkDrift", out var checkDriftElement))
        {
            if (checkDriftElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new SnapshotCatalogContractException("checkDrift must be a boolean.");
            checkDrift = checkDriftElement.GetBoolean();
        }

        switch (action)
        {
            case SnapshotCatalogAction.List when target != null || name != null:
                throw new SnapshotCatalogContractException("list does not accept targetSnapshotId or name.");
            case SnapshotCatalogAction.Pin when target == null:
                throw new SnapshotCatalogContractException("pin requires targetSnapshotId.");
            case SnapshotCatalogAction.Unpin or SnapshotCatalogAction.Evict when target == null:
                throw new SnapshotCatalogContractException($"{action.ToString().ToLowerInvariant()} requires targetSnapshotId.");
            case SnapshotCatalogAction.Pin when checkDrift:
            case SnapshotCatalogAction.Unpin when checkDrift:
            case SnapshotCatalogAction.Evict when checkDrift:
                throw new SnapshotCatalogContractException("checkDrift is valid only with action=list.");
            case SnapshotCatalogAction.Unpin or SnapshotCatalogAction.Evict when name != null:
                throw new SnapshotCatalogContractException("name is valid only with action=pin.");
        }

        return new SnapshotCatalogRequest(action, target, name, checkDrift);
    }
}
