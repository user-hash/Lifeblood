namespace Lifeblood.Domain.Results;

/// <summary>
/// Computed metadata layout for a single struct: field offsets, field sizes,
/// alignment, packing, and total size. Exact for blittable sequential / explicit
/// structs whose field types are known; advisory for Auto layout, reference-
/// bearing fields, and non-blittable primitive marshal shapes.
/// INV-STRUCT-LAYOUT-001.
/// </summary>
public sealed class StructLayoutReport
{
    public required string TypeId { get; init; }
    public required string TypeName { get; init; }
    public required string LayoutKind { get; init; }
    public required int Pack { get; init; }
    public int? DeclaredSize { get; init; }
    public int? Size { get; init; }
    public int? Alignment { get; init; }
    public required int PointerSize { get; init; }
    public required bool IsUnmanaged { get; init; }
    public required bool IsBlittable { get; init; }
    public required string Confidence { get; init; }
    public required string[] Limitations { get; init; }
    public required StructLayoutField[] Fields { get; init; }
}

/// <summary>One instance field inside a <see cref="StructLayoutReport"/>.</summary>
public sealed class StructLayoutField
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int? Offset { get; init; }
    public int? Size { get; init; }
    public int? Alignment { get; init; }
    public int? FixedBufferLength { get; init; }
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public required string[] Limitations { get; init; }
}

public static class StructLayoutConfidence
{
    public const string Exact = "Exact";
    public const string Advisory = "Advisory";
}

public static class StructLayoutKind
{
    public const string Sequential = "Sequential";
    public const string Explicit = "Explicit";
    public const string Auto = "Auto";
}
