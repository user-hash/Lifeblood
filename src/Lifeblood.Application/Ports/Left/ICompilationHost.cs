using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Provides compilation-level capabilities: diagnostics, compile-checking, reference finding.
/// Language-agnostic contract — Roslyn implements with CSharpCompilation.
/// </summary>
public interface ICompilationHost
{
    bool IsAvailable { get; }
    DiagnosticInfo[] GetDiagnostics(string? moduleName = null);

    /// <summary>
    /// File-scoped or module-scoped diagnostics. <see cref="DiagnosticsRequest.FilePath"/>
    /// (relative to the workspace root, or absolute) restricts the result to one
    /// source file — useful for verifying a single edited file without drowning
    /// in a whole-project dump. <see cref="DiagnosticsRequest.ModuleName"/>
    /// disambiguates which compilation contains the file when the same path
    /// appears in multiple modules. Either field may be omitted; both omitted
    /// is equivalent to the parameterless overload.
    /// </summary>
    DiagnosticInfo[] GetDiagnostics(DiagnosticsRequest request);

    /// <summary>
    /// Diagnostics plus the preprocessor scope they were bound under. Same
    /// scoping rules as <see cref="GetDiagnostics(DiagnosticsRequest)"/>;
    /// the returned <see cref="DiagnosticsReport.DefinesActive"/> tells the
    /// caller whether a finding is Editor-only noise or a release-build
    /// risk without re-running the host under a different define set.
    /// File-scope and single-module-scope return the owning compilation's
    /// defines; project-wide scope returns the sorted, deduplicated union
    /// across every loaded compilation. INV-DIAGNOSTIC-ENVELOPE-DEFINES-001
    /// / LB-INBOX-008.
    /// </summary>
    DiagnosticsReport GetDiagnosticsReport(DiagnosticsRequest request);

    CompileCheckResult CompileCheck(string code, string? moduleName = null);

    /// <summary>
    /// Typed-request overload of <see cref="CompileCheck(string, string?)"/>.
    /// File-mode (<see cref="CompileCheckRequest.FilePath"/> set) auto-detects
    /// the owning compilation by matching the path against each compilation's
    /// syntax-tree paths and swaps the file's existing tree for the on-disk
    /// content, so module-owned files compile-check against their real
    /// reference set instead of being added as a duplicate snippet tree to
    /// some arbitrary first compilation. Snippet mode (<see cref="CompileCheckRequest.Code"/>
    /// set) preserves the legacy snippet-wrapping behavior.
    /// </summary>
    CompileCheckResult CompileCheck(CompileCheckRequest request);

    /// <summary>
    /// Find every source location that references the given symbol. Defaults
    /// to references only — declaration sites are excluded unless the caller
    /// opts in via <see cref="FindReferences(string, FindReferencesOptions)"/>.
    /// </summary>
    ReferenceLocation[] FindReferences(string symbolId);

    /// <summary>
    /// Find references with explicit operation policy
    /// (<see cref="FindReferencesOptions.IncludeDeclarations"/> etc.).
    /// "Include declarations or not" is a write-side reference-search policy
    /// — it is NOT something the resolver can decide from a symbol id alone.
    /// </summary>
    ReferenceLocation[] FindReferences(string symbolId, FindReferencesOptions options);

    /// <summary>Find where a symbol is declared (definition site).</summary>
    DefinitionLocation? FindDefinition(string symbolId);

    /// <summary>
    /// Per-member reference coverage for an enum type. Walks every
    /// loaded compilation once, classifies each reference site by parent
    /// syntax (production / comparison / switch-pattern / other) and
    /// returns a row per declared member. The dogfood case: a
    /// state-machine or telemetry enum has values declared on the type
    /// but never produced by runtime code — <c>find_references</c> hits
    /// them as consumers (switch arms, equality checks) which masks the
    /// drift. This call answers "is this value ever assigned to
    /// anything?" in one step. Returns null when
    /// <paramref name="enumTypeId"/> does not resolve to an enum type
    /// in any loaded compilation. INV-ENUM-COVERAGE-001 /
    /// LB-TRACK-20260514-003.
    /// </summary>
    EnumCoverageReport? GetEnumCoverage(string enumTypeId);

    /// <summary>
    /// Generic static-initializer table extraction. Walks every
    /// <c>static</c> field / property on <paramref name="typeId"/>
    /// whose initializer Roslyn surfaces as
    /// <c>IArrayCreationOperation</c>, <c>ICollectionExpressionOperation</c>,
    /// or a single <c>IObjectCreationOperation</c>, and emits one
    /// <see cref="StaticTable"/> per match with row + cell facts sourced
    /// from <c>SemanticModel.GetOperation</c> only. Cell values classify
    /// into the canonical kind set in <c>StaticTableValueKind</c>;
    /// unclassified shapes carry <c>Kind = Computed</c> with raw source
    /// text as the eternal provenance. The host MUST NOT reference any
    /// consumer-domain vocabulary in classification logic — pinned by
    /// the name-leakage ratchet test. Returns null when
    /// <paramref name="typeId"/> does not resolve to a type in any
    /// loaded compilation. INV-EXTRACT-STATIC-TABLES-001.
    /// </summary>
    StaticTableReport? GetStaticTables(string typeId, StaticTablesOptions options);

    /// <summary>
    /// Per-construction-site slot coverage for a target type. For each
    /// <c>new TargetType { ... }</c> or <c>new TargetType()</c> + statement-
    /// level assignment site, walks the containing method's
    /// <c>IOperation</c> tree and reports which of the target's public
    /// mutable slot members are assigned at that site. Operation-tree only;
    /// never regex, never syntax-text. Confidence is per-site: inline
    /// initializer or non-aliased single-method statement chain is
    /// <c>Proven</c>; factory / aliased / branched MAY-assign shapes are
    /// <c>Advisory</c> with the bumping shape named in
    /// <c>SiteLimitations</c>. Returns null when
    /// <paramref name="targetTypeId"/> does not resolve to a type in any
    /// loaded compilation. INV-ASSIGNMENT-COVERAGE-001.
    /// </summary>
    AssignmentCoverageReport? GetAssignmentCoverage(string targetTypeId, AssignmentCoverageOptions options);

    /// <summary>
    /// Per-call-site argument facts for a target method or constructor. Walks
    /// every loaded compilation's <c>IInvocationOperation</c> /
    /// <c>IObjectCreationOperation</c>, matches the bound callee against the
    /// target by canonical id, and reports each argument's bound parameter,
    /// author-supplied-vs-default-filled status, classified value kind, and raw
    /// text, plus a per-parameter supplied/omitted histogram across all sites.
    /// The dogfood case: a new optional parameter or richer overload exists but
    /// every call site still uses the old argument shape — semantic
    /// "callee is referenced" checks look green while the parameter is omitted
    /// by 7/7 sites. Operation-tree only; never regex. Returns null when
    /// <paramref name="symbolId"/> does not resolve to a method or constructor
    /// in any loaded compilation. INV-CALLSITE-ARGS-001.
    /// </summary>
    CallsiteArgumentsReport? GetCallsiteArguments(string symbolId, CallsiteArgumentsOptions options);

    /// <summary>
    /// Dead-WIRE audit: members that are referenced but structurally unplugged.
    /// One operation-tree pass over every loaded compilation classifies each
    /// field/property reference as read or write and accumulates per-member
    /// counts; emits private/internal fields READ with zero writes
    /// (read-but-never-written) and delegate-typed slots never assigned anywhere
    /// (never-wired). Complements <c>dead_code</c> (UNreferenced symbols) by
    /// catching the opposite failure — the recurring extraction-severed-wiring
    /// bug class. Advisory: reflection / Unity serialized (YAML) / runtime
    /// assignment is invisible to static analysis. Always returns a report (never
    /// null); empty findings when nothing qualifies. INV-WIRE-AUDIT-001.
    /// </summary>
    WireAuditReport GetWireAudit(WireAuditOptions options);

    /// <summary>
    /// Dormant feature-switch audit: boolean fields / settable boolean properties
    /// that gate branches but whose value is pinned to the default because nothing
    /// reachable in the graph flips them. One operation-tree pass classifies each
    /// switch reference read-vs-write, locates the branch conditions it gates,
    /// records each flipping write's bucket + containing member, and tallies call
    /// sites so an unreachable mutator (e.g. a public <c>SetGrammarMode</c> with
    /// zero callers) downgrades the switch to <c>AlwaysDefaultInGraph</c>. Verdicts:
    /// <c>AlwaysDefaultInGraph</c> / <c>TestOnlyActivation</c> / <c>RuntimeMutable</c>.
    /// Complements <c>wire_audit</c> (zero wiring) and <c>dead_code</c>
    /// (unreferenced) by catching "looks shipped, never activated". Advisory:
    /// reflection / Unity serialized (YAML) / runtime / config-driven assignment is
    /// invisible to static analysis. Always returns a report (never null); empty
    /// when nothing qualifies. INV-FEATURE-SWITCH-001.
    /// </summary>
    FeatureSwitchReport GetFeatureSwitchAudit(FeatureSwitchAuditOptions options);

    /// <summary>
    /// Declared-member count for a single type. <c>semantics</c> selects
    /// <c>reflectionDeclared</c> (bit-exact System.Reflection DeclaredOnly: methods
    /// incl. accessors + ctors + fields + properties + events, compiler-generated /
    /// backing-field filtered, nested types excluded, implicit default ctor counted)
    /// or <c>sourceSymbols</c> (source-declared member symbols incl. nested types,
    /// excl. synthesized accessors/backing — the Lifeblood graph child semantics).
    /// Returns null when the id does not resolve to a source type.
    /// INV-MEMBER-COUNT-001.
    /// </summary>
    MemberCountReport? GetMemberCount(string typeId, string semantics);

    /// <summary>Find all types that implement an interface or override a virtual member.</summary>
    string[] FindImplementations(string symbolId);

    /// <summary>Resolve the symbol at a source position (file + line + column).</summary>
    SymbolAtPosition? GetSymbolAtPosition(string filePath, int line, int column);

    /// <summary>Get XML documentation for a symbol.</summary>
    string GetDocumentation(string symbolId);
}

/// <summary>
/// Scope filter for <see cref="ICompilationHost.GetDiagnostics(DiagnosticsRequest)"/>.
/// Both fields are optional. When <see cref="FilePath"/> is set, the result is
/// limited to diagnostics whose syntax-tree path matches the requested file.
/// When <see cref="ModuleName"/> is set, the search is restricted to that
/// module's compilation; when both are set, the file must live inside the
/// named module. Path comparison is case-insensitive on Windows-style paths
/// and uses both relative-to-project and absolute matching so a caller can
/// pass either form.
/// </summary>
public sealed class DiagnosticsRequest
{
    public string? FilePath { get; init; }
    public string? ModuleName { get; init; }
}

/// <summary>
/// Options for <see cref="ICompilationHost.FindReferences(string, FindReferencesOptions)"/>.
/// Each flag is a deliberate behavior choice on the live-walker side, NOT
/// something the resolver can decide from a symbol id alone.
///
/// Reserved as a separate record so future per-call policy (filter scope,
/// match case, declaration-only mode, etc.) can be added without further
/// signature changes.
/// </summary>
public sealed class FindReferencesOptions
{
    public static readonly FindReferencesOptions Default = new();

    /// <summary>
    /// When true, the result includes a synthetic <c>"(declaration)"</c>
    /// entry for every source location where the symbol is declared. For
    /// partial types, this means one entry per partial declaration file
    /// (Roslyn's <c>ISymbol.Locations</c> returns one location per partial).
    /// For non-partial symbols, exactly one declaration entry. Default
    /// false preserves the existing pure-references-only behavior.
    /// </summary>
    public bool IncludeDeclarations { get; init; }
}
