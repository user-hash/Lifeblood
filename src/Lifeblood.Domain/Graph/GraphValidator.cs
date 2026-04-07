namespace Lifeblood.Domain.Graph;

/// <summary>
/// Rejects malformed graphs before analysis. Pure validation, no mutation.
/// Returns machine-readable errors with codes for programmatic handling.
/// </summary>
public static class GraphValidator
{
    public static GraphValidationError[] Validate(SemanticGraph graph)
    {
        var errors = new List<GraphValidationError>();
        var symbolIds = new HashSet<string>(graph.Symbols.Length, StringComparer.Ordinal);

        ValidateSymbols(graph, symbolIds, errors);
        ValidateEdges(graph, symbolIds, errors);
        ValidateParentReferences(graph, symbolIds, errors);

        return errors.ToArray();
    }

    private static void ValidateSymbols(
        SemanticGraph graph, HashSet<string> symbolIds, List<GraphValidationError> errors)
    {
        for (int i = 0; i < graph.Symbols.Length; i++)
        {
            var s = graph.Symbols[i];

            if (string.IsNullOrEmpty(s.Id))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "EMPTY_SYMBOL_ID",
                    Message = $"Symbol at index {i} has empty ID",
                });
                continue;
            }

            if (!symbolIds.Add(s.Id))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DUPLICATE_SYMBOL_ID",
                    Message = $"Duplicate symbol ID: {s.Id}",
                    SymbolId = s.Id,
                });
            }

            if (string.IsNullOrEmpty(s.Name))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "EMPTY_SYMBOL_NAME",
                    Message = $"Symbol '{s.Id}' has empty Name",
                    SymbolId = s.Id,
                });
            }
        }
    }

    private static void ValidateEdges(
        SemanticGraph graph, HashSet<string> symbolIds, List<GraphValidationError> errors)
    {
        for (int i = 0; i < graph.Edges.Length; i++)
        {
            var e = graph.Edges[i];

            if (string.IsNullOrEmpty(e.SourceId))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "EMPTY_EDGE_SOURCE",
                    Message = $"Edge at index {i} has empty SourceId",
                    EdgeIndex = i,
                });
            }
            else if (!symbolIds.Contains(e.SourceId))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DANGLING_EDGE_SOURCE",
                    Message = $"Edge at index {i}: SourceId '{e.SourceId}' not found in symbols",
                    EdgeIndex = i,
                });
            }

            if (string.IsNullOrEmpty(e.TargetId))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "EMPTY_EDGE_TARGET",
                    Message = $"Edge at index {i} has empty TargetId",
                    EdgeIndex = i,
                });
            }
            else if (!symbolIds.Contains(e.TargetId))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DANGLING_EDGE_TARGET",
                    Message = $"Edge at index {i}: TargetId '{e.TargetId}' not found in symbols",
                    EdgeIndex = i,
                });
            }

            // Self-referencing Calls edges are valid (recursion).
            // Only flag self-referencing dependency/structural edges.
            if (!string.IsNullOrEmpty(e.SourceId) && e.SourceId == e.TargetId
                && e.Kind != EdgeKind.Calls)
            {
                errors.Add(new GraphValidationError
                {
                    Code = "SELF_REFERENCING_EDGE",
                    Message = $"Edge at index {i}: self-reference on '{e.SourceId}'",
                    EdgeIndex = i,
                });
            }
        }

        // Duplicate edge detection (same source + target + kind)
        var edgeSet = new HashSet<(string, string, EdgeKind)>();
        for (int i = 0; i < graph.Edges.Length; i++)
        {
            var e = graph.Edges[i];
            if (!edgeSet.Add((e.SourceId, e.TargetId, e.Kind)))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DUPLICATE_EDGE",
                    Message = $"Edge at index {i}: duplicate {e.Kind} from '{e.SourceId}' to '{e.TargetId}'",
                    EdgeIndex = i,
                });
            }
        }
    }

    private static void ValidateParentReferences(
        SemanticGraph graph, HashSet<string> symbolIds, List<GraphValidationError> errors)
    {
        for (int i = 0; i < graph.Symbols.Length; i++)
        {
            var s = graph.Symbols[i];
            if (!string.IsNullOrEmpty(s.ParentId) && !symbolIds.Contains(s.ParentId))
            {
                errors.Add(new GraphValidationError
                {
                    Code = "DANGLING_PARENT_ID",
                    Message = $"Symbol '{s.Id}': ParentId '{s.ParentId}' not found in symbols",
                    SymbolId = s.Id,
                });
            }
        }
    }
}

public sealed class GraphValidationError
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? SymbolId { get; init; }
    public int? EdgeIndex { get; init; }
}
