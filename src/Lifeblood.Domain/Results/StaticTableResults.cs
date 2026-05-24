namespace Lifeblood.Domain.Results;

/// <summary>
/// Static collection-shaped initializer report for one type. Surfaces
/// rows + cells extracted from <c>static</c> field / property
/// initializers (arrays, collection expressions, single-object table
/// shapes) so a caller can join row-data to source spans without
/// re-parsing the file. Tool is intentionally generic — it never sees
/// consumer-domain row vocabulary; cell kinds classify into the
/// canonical set in <see cref="StaticTableValueKind"/> with
/// <c>Computed</c> as the eternal escape hatch. INV-EXTRACT-STATIC-TABLES-001.
/// </summary>
public sealed class StaticTableReport
{
    /// <summary>Canonical id of the type the report covers.</summary>
    public required string TypeId { get; init; }

    /// <summary>One entry per static field / property whose initializer matched a table-shape container.</summary>
    public required StaticTable[] Tables { get; init; }

    /// <summary>True when extraction stopped at <c>maxTables</c> and additional tables exist on the type.</summary>
    public required bool TablesTruncated { get; init; }
}

/// <summary>
/// One static initializer table on a type. The owning member is a
/// <c>field:</c> or <c>property:</c> symbol; container kind distinguishes
/// the Roslyn shape that backed the rows.
/// </summary>
public sealed class StaticTable
{
    /// <summary>Canonical id of the owning field or property.</summary>
    public required string MemberId { get; init; }

    /// <summary>Short member name (e.g. <c>Features</c>) for display.</summary>
    public required string MemberName { get; init; }

    /// <summary>Source file path of the member declaration.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line number of the member declaration.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number of the member declaration.</summary>
    public required int Column { get; init; }

    /// <summary>Name of the compilation module that owns the member.</summary>
    public required string ModuleName { get; init; }

    /// <summary>
    /// Container shape Roslyn surfaced for the initializer. One of
    /// <see cref="StaticTableContainerKind.Array"/>,
    /// <see cref="StaticTableContainerKind.CollectionExpression"/>,
    /// <see cref="StaticTableContainerKind.ObjectCreation"/>.
    /// </summary>
    public required string ContainerKind { get; init; }

    /// <summary>
    /// Canonical id of the element type (array element / collection
    /// element / single-row constructed type). Null when Roslyn could
    /// not resolve the element type at the initializer site.
    /// </summary>
    public string? ElementTypeId { get; init; }

    /// <summary>Rows in source order. For <c>ObjectCreation</c> containers this carries exactly one row.</summary>
    public required StaticTableRow[] Rows { get; init; }

    /// <summary>True when extraction stopped at <c>maxRows</c> and additional rows exist in the table.</summary>
    public required bool RowsTruncated { get; init; }
}

/// <summary>
/// One row in a static initializer table. For array / collection
/// containers, each element is a row; for single-object containers the
/// table itself is the row. Rows are <c>IObjectCreationOperation</c>
/// shaped when a constructor is known; literal-only rows (e.g. a plain
/// <c>string</c> array) carry <see cref="ConstructorId"/>=<c>null</c>
/// and a single synthesized cell at <see cref="Cells"/>[0].
/// </summary>
public sealed class StaticTableRow
{
    /// <summary>0-based position in the parent table.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Source file the row is authored in (matches the table's file in the common case).</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line number of the row's authoring span.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number of the row's authoring span.</summary>
    public required int Column { get; init; }

    /// <summary>Canonical id of the constructor invoked by the row. Null for non-constructed rows (literal arrays).</summary>
    public string? ConstructorId { get; init; }

    /// <summary>One cell per constructor argument, in declaration order. Empty for literal-only rows — see <see cref="Value"/>.</summary>
    public required StaticTableCell[] Cells { get; init; }

    /// <summary>
    /// Classified value for non-constructor rows (literal arrays /
    /// collection-expression scalar elements). Populated when
    /// <see cref="ConstructorId"/> is null; null otherwise. Keeps the
    /// row shape uniform across constructed and literal rows without
    /// fabricating a fake cell.
    /// </summary>
    public StaticTableValue? Value { get; init; }
}

/// <summary>
/// One cell in a static initializer row. A cell pairs the constructor
/// parameter it bound to with the value Roslyn classified at the
/// authoring site.
/// </summary>
public sealed class StaticTableCell
{
    /// <summary>Constructor parameter name this cell bound to. Null when positional and unresolvable.</summary>
    public string? ParameterName { get; init; }

    /// <summary>0-based parameter position in the constructor signature.</summary>
    public required int Position { get; init; }

    /// <summary>
    /// Roslyn argument kind. One of
    /// <see cref="StaticTableArgumentKind.Explicit"/>,
    /// <see cref="StaticTableArgumentKind.DefaultValue"/>,
    /// <see cref="StaticTableArgumentKind.ParamArray"/>. Mirrors
    /// <c>IArgumentOperation.ArgumentKind</c> so a caller can tell a
    /// constructor default apart from an explicit author-supplied value
    /// without round-tripping to the source span.
    /// </summary>
    public required string ArgumentKind { get; init; }

    /// <summary>Typed cell value with classification + provenance.</summary>
    public required StaticTableValue Value { get; init; }
}

/// <summary>
/// One classified value at an authoring site. Exactly one typed payload
/// field is populated per <see cref="Kind"/>; <see cref="RawText"/> is
/// always present so a caller can fall back to the source span when
/// the kind is <c>Computed</c> or when a downstream consumer wants
/// audit provenance.
/// </summary>
public sealed class StaticTableValue
{
    /// <summary>
    /// Canonical kind string from <see cref="StaticTableValueKind"/>.
    /// The set is open for non-breaking extension: callers MUST tolerate
    /// unknown kinds by falling back to <see cref="RawText"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Source span text Roslyn authored. Always populated; load-bearing for the <c>Computed</c> fallback.</summary>
    public required string RawText { get; init; }

    /// <summary>Source file containing the value.</summary>
    public required string FilePath { get; init; }

    /// <summary>1-based line number of the value's authoring span.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number of the value's authoring span.</summary>
    public required int Column { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>Bool</c>.</summary>
    public bool? BoolValue { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>String</c>.</summary>
    public string? StringValue { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>Number</c>. Stored as <c>double</c> for wire simplicity; callers needing integer precision read <see cref="RawText"/>.</summary>
    public double? NumberValue { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>EnumMember</c>. Canonical field id of the enum constant.</summary>
    public string? EnumMemberId { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>EnumFlags</c>. Canonical field ids of each composed flag, in left-to-right authoring order.</summary>
    public string[]? EnumFlagMemberIds { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>MethodGroup</c>. Canonical method id of the referenced delegate target.</summary>
    public string? MethodGroupId { get; init; }

    /// <summary>
    /// Populated when <see cref="Kind"/> is <c>MethodGroup</c> AND the delegate target carries a source declaration
    /// (<see cref="Microsoft.CodeAnalysis.ISymbol.DeclaringSyntaxReferences"/> non-empty) AND at least one return position in
    /// the target's body resolves to an enum-flag value — either a single enum-const <c>IFieldReferenceOperation</c> or
    /// an <c>|</c>-composed <c>IBinaryOperation</c> tree of enum-const leaves. The array carries the UNION of enum-flag
    /// member ids reachable across all return paths, sorted by canonical id for deterministic output. Null when the
    /// target has no source decl (compiled metadata), when the body has no <c>return</c> producing an enum-flag value, or
    /// when every return position falls back to <c>Computed</c>. INV-METHOD-FLAG-SUMMARY-001.
    /// </summary>
    /// <remarks>
    /// This field surfaces the narrow same-compilation return-position view only. For broader queries —
    /// flag-field references anywhere in the body (not just <c>return</c> positions), cross-compilation
    /// delegate targets, or transitive walks through helper-call chains — compose
    /// <c>lifeblood_dependencies(methodGroupId)</c>: filter outbound <c>References</c> edges by enum-type-id
    /// prefix on the caller side. The "does method M reference every flag in row R's cell?" question is a
    /// pure client-side set-relation over two existing emissions; this field is one shape,
    /// <c>lifeblood_dependencies</c> is the other. INV-FLAG-COVERAGE-COMPOSITION-001 pins the recipe and
    /// forbids verdict-shaped wire tools on the join.
    /// </remarks>
    public string[]? MethodReturnFlagIds { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>FieldReference</c>. Canonical field id of the referenced non-enum static field.</summary>
    public string? FieldReferenceId { get; init; }

    /// <summary>Populated when <see cref="Kind"/> is <c>Array</c>. Element values in source order.</summary>
    public StaticTableValue[]? ArrayElements { get; init; }
}

/// <summary>
/// Canonical cell-kind string set. Open for non-breaking extension —
/// new kinds may be added; callers MUST tolerate unknown kinds. The
/// <c>Computed</c> bucket is the eternal escape hatch: when Roslyn
/// returns an operation shape the extractor does not classify, the
/// cell carries <see cref="StaticTableValueKind.Computed"/> with
/// <see cref="StaticTableValue.RawText"/> as the only provenance.
/// </summary>
public static class StaticTableValueKind
{
    public const string Null = "Null";
    public const string Bool = "Bool";
    public const string String = "String";
    public const string Number = "Number";
    public const string EnumMember = "EnumMember";
    public const string EnumFlags = "EnumFlags";
    public const string MethodGroup = "MethodGroup";
    public const string FieldReference = "FieldReference";
    public const string Array = "Array";
    public const string Computed = "Computed";
}

/// <summary>Canonical container-shape strings. Open set (eternal extension same rules as <see cref="StaticTableValueKind"/>).</summary>
public static class StaticTableContainerKind
{
    public const string Array = "Array";
    public const string CollectionExpression = "CollectionExpression";
    public const string ObjectCreation = "ObjectCreation";
}

/// <summary>
/// Roslyn argument kind, surfaced as a stable string so the wire is
/// not coupled to a specific <c>Microsoft.CodeAnalysis</c> version.
/// Names mirror <c>IArgumentOperation.ArgumentKind</c>.
/// </summary>
public static class StaticTableArgumentKind
{
    public const string Explicit = "Explicit";
    public const string DefaultValue = "DefaultValue";
    public const string ParamArray = "ParamArray";
}

/// <summary>
/// Per-call extraction caps. INV-STATIC-TABLES-DEFAULT-MAXROWS-001 +
/// INV-STATIC-TABLES-SUMMARIZE-001. Host MUST clamp negative / zero
/// values so a caller cannot disable extraction by passing
/// <c>maxRows = 0</c>.
/// </summary>
public sealed class StaticTablesOptions
{
    /// <summary>Optional member-name filter.</summary>
    public string? MemberName { get; init; }

    /// <summary>Max rows per table. Adapter-side default applies when unset.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Max tables per type. Adapter-side default applies when unset.</summary>
    public int? MaxTables { get; init; }

    /// <summary>INV-STATIC-TABLES-SUMMARIZE-001. Forces compact caps when true.</summary>
    public bool? Summarize { get; init; }
}
