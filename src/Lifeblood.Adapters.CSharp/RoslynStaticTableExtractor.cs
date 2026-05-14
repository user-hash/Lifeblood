using Lifeblood.Domain.Results;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lifeblood.Adapters.CSharp;

/// <summary>
/// Generic static-initializer table extractor. Walks each <c>static</c>
/// field / property declaration on a target type, asks Roslyn for the
/// initializer's <see cref="IOperation"/> tree, and classifies row
/// constructors + cell values into <see cref="StaticTableReport"/>
/// facts. The extractor is intentionally consumer-domain-blind — any
/// vocabulary outside the canonical kind set in
/// <see cref="StaticTableValueKind"/> is forbidden and pinned by the
/// name-leakage ratchet test. INV-EXTRACT-STATIC-TABLES-001.
/// </summary>
internal static class RoslynStaticTableExtractor
{
    internal const int DefaultMaxRows = 1024;
    internal const int DefaultMaxTables = 64;

    /// <summary>
    /// Build a <see cref="StaticTableReport"/> for the given resolved
    /// type. Caller is responsible for type-id resolution; this method
    /// only enumerates the type's static fields / properties + walks
    /// their initializer operations. Per-kind classification grows
    /// fixture-by-fixture in subsequent commits — the initial entry
    /// point returns an empty result so the port is callable end-to-end
    /// before any kind lands.
    /// </summary>
    internal static StaticTableReport? Extract(
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        INamedTypeSymbol typeSymbol,
        string typeId,
        StaticTablesOptions options,
        Func<ISymbol, string> buildSymbolId)
    {
        _ = compilations; _ = typeSymbol; _ = options; _ = buildSymbolId;
        _ = ClampPositive(options.MaxRows, DefaultMaxRows);
        _ = ClampPositive(options.MaxTables, DefaultMaxTables);

        return new StaticTableReport
        {
            TypeId = typeId,
            Tables = Array.Empty<StaticTable>(),
            TablesTruncated = false,
        };
    }

    private static int ClampPositive(int? requested, int @default)
    {
        if (!requested.HasValue) return @default;
        return requested.Value > 0 ? requested.Value : @default;
    }
}
