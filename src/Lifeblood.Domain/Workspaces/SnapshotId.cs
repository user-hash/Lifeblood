namespace Lifeblood.Domain.Workspaces;

/// <summary>
/// Opaque identity of one committed workspace publication. Equality/reuse of
/// analysis inputs belongs to AnalysisKey; this value identifies the exact
/// published snapshot and therefore stays distinct across refreshes and daemon
/// restarts even when generation counters or source content happen to match.
/// </summary>
public sealed record SnapshotId
{
    private const string Prefix = "snap_";

    private SnapshotId(string value)
    {
        Value = value;
    }

    public static SnapshotId None { get; } = new("");

    public string Value { get; }

    public bool IsNone => Value.Length == 0;

    public static SnapshotId New()
        => new(Prefix + Guid.NewGuid().ToString("N"));

    public static bool TryParse(string? value, out SnapshotId snapshotId)
    {
        snapshotId = None;
        if (value == null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(value[Prefix.Length..], "N", out var parsed))
        {
            return false;
        }

        snapshotId = new SnapshotId(Prefix + parsed.ToString("N"));
        return true;
    }

    public override string ToString() => Value;
}
