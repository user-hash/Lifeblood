namespace Lifeblood.Domain.Results;

/// <summary>
/// Impact within caller-selected traversal boundaries. Reached boundary
/// symbols remain affected; their incoming dependency paths are not expanded.
/// </summary>
public sealed class BlastRadiusTraversalResult
{
    public required BlastRadiusResult Impact { get; init; }

    /// <summary>
    /// Reached symbols whose expansion stopped, including owned members of
    /// selected boundaries. This is not a count of unvisited descendants.
    /// </summary>
    public required string[] StoppedAtSymbolIds { get; init; }
}
