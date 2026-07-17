namespace Lifeblood.Domain.Results;

/// <summary>
/// One occurrence-level semantic operation emitted by a language adapter.
/// These facts are deliberately separate from the architecture graph: graph
/// edges answer symbol coupling questions, while operation facts preserve the
/// call arguments, value origins, control context, and source occurrence that
/// contract rules need. The vocabulary is language-neutral and open for
/// additive extension. INV-OPERATION-FACTS-001.
/// </summary>
public sealed class OperationFact
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string ModuleName { get; init; }
    public required string ProfileScope { get; init; }
    public required string ContainingSymbolId { get; init; }
    public string? TargetSymbolId { get; init; }
    public string? ResultType { get; init; }
    public string? Operator { get; init; }
    public required OperationSourceSpan Source { get; init; }
    public required bool IsImplicit { get; init; }
    public OperationInputFact[] Inputs { get; init; } = Array.Empty<OperationInputFact>();
    public OperationControlContext[] ControlContexts { get; init; } = Array.Empty<OperationControlContext>();
}

/// <summary>One named input slot on an <see cref="OperationFact"/>.</summary>
public sealed class OperationInputFact
{
    public required string Role { get; init; }
    public int? Ordinal { get; init; }
    public string? ParameterId { get; init; }
    public string? ParameterName { get; init; }
    public required bool AuthorSupplied { get; init; }
    public required OperationValueFact Value { get; init; }
}

/// <summary>
/// Compact value provenance for an operation input. <see cref="SourceSymbolIds"/>
/// records every symbol contributing to the expression in deterministic order;
/// <see cref="ConversionTypes"/> records explicit and implicit conversion
/// destinations, and <see cref="Operators"/> records nested value operators,
/// without retaining compiler objects.
/// </summary>
public sealed class OperationValueFact
{
    public required string Kind { get; init; }
    public string? SymbolId { get; init; }
    public string? Type { get; init; }
    public string? ConstantValue { get; init; }
    public string? Expression { get; init; }
    public required bool IsCompileTimeConstant { get; init; }
    public string[] SourceSymbolIds { get; init; } = Array.Empty<string>();
    public string[] ConversionTypes { get; init; } = Array.Empty<string>();
    public string[] Operators { get; init; } = Array.Empty<string>();
    public OperationConstantFact[] Constants { get; init; } = Array.Empty<OperationConstantFact>();
}

/// <summary>
/// One constant occurrence contributing to a value expression. The adapter
/// preserves literal versus named-policy provenance and numeric finiteness
/// without assigning a consumer domain or deciding whether the value is safe.
/// </summary>
public sealed class OperationConstantFact
{
    public required string Origin { get; init; }
    public string? SymbolId { get; init; }
    public string? Type { get; init; }
    public string? Value { get; init; }
    public required string NumericClassification { get; init; }
    public string? Expression { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

/// <summary>Control-flow ancestor surrounding an operation occurrence.</summary>
public sealed class OperationControlContext
{
    public required string Kind { get; init; }
    public string? BranchArm { get; init; }
    public string? Condition { get; init; }
    public OperationValueFact? ConditionValue { get; init; }
    public string[] Operators { get; init; } = Array.Empty<string>();
    public OperationControlPredicate[] Predicates { get; init; } = Array.Empty<OperationControlPredicate>();
    public required OperationSourceSpan Source { get; init; }
}

/// <summary>
/// One operator-local predicate inside a control condition. Keeping source
/// symbols attached to their exact operator prevents a compound condition's
/// unrelated comparison from being mistaken for a guard.
/// </summary>
public sealed class OperationControlPredicate
{
    public required string Operator { get; init; }
    public string? Expression { get; init; }
    public string[] SourceSymbolIds { get; init; } = Array.Empty<string>();
    public OperationValueFact? LeftValue { get; init; }
    public OperationValueFact? RightValue { get; init; }
    public required OperationSourceSpan Source { get; init; }
}

/// <summary>Stable, adapter-neutral source occurrence.</summary>
public sealed class OperationSourceSpan
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
}

/// <summary>Bounded scan request for an operation-fact provider.</summary>
public sealed class OperationFactQuery
{
    public string? ModuleScope { get; init; }
    public string? ProfileScope { get; init; }
    public IReadOnlyList<string>? FilePaths { get; init; }
    public IReadOnlyList<string>? ContainingSymbolIds { get; init; }
    public IReadOnlyList<string>? TargetSymbolIds { get; init; }
    public IReadOnlyList<string>? IncludeKinds { get; init; }
    public IReadOnlyList<OperationFactSelector>? Selectors { get; init; }
    public bool IncludeImplicit { get; init; }
    public int MaxFacts { get; init; } = 10_000;
}

/// <summary>
/// One adapter-neutral disjunctive occurrence selector. Populated dimensions
/// are ANDed inside a selector; selectors are ORed. This lets one fact pass
/// stay narrow when selected rule families mix bound calls with targetless
/// operations such as element access and built-in binary operators.
/// </summary>
public sealed class OperationFactSelector
{
    public string[] IncludeKinds { get; init; } = Array.Empty<string>();
    public string[] TargetSymbolIds { get; init; } = Array.Empty<string>();
    public string[] ContainingSymbolIds { get; init; } = Array.Empty<string>();
    public string[] Operators { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Truthful execution receipt for a streaming operation-fact scan. Facts flow
/// through the caller-provided consumer and are never retained by this record.
/// </summary>
public sealed class OperationFactScanReceipt
{
    public required string Status { get; init; }
    public string? RejectionReason { get; init; }
    public required string ProfileScope { get; init; }
    public string[] AvailableProfiles { get; init; } = Array.Empty<string>();
    public required string ExecutionMode { get; init; }
    public required bool InputIdentityVerifiedAtStart { get; init; }
    public required int AdditionalSemanticBaseCount { get; init; }
    public required int CompiledModuleCount { get; init; }
    public required int ScannedModuleCount { get; init; }
    public required int ScannedFileCount { get; init; }
    public required int ObservedOperationCount { get; init; }
    public required int EmittedFactCount { get; init; }
    public required bool Truncated { get; init; }
    public required bool StoppedByConsumer { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

/// <summary>Open operation-fact scan status vocabulary.</summary>
public static class OperationFactScanStatus
{
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
}

/// <summary>Open operation-fact scan rejection-reason vocabulary.</summary>
public static class OperationFactRejectionReason
{
    public const string InputDrift = "InputDrift";
}

/// <summary>Open operation-fact execution-mode vocabulary.</summary>
public static class OperationFactExecutionMode
{
    public const string RetainedCompilation = "RetainedCompilation";
    public const string EphemeralProfileCompilation = "EphemeralProfileCompilation";
}

/// <summary>Open operation-kind vocabulary.</summary>
public static class OperationFactKind
{
    public const string Call = "Call";
    public const string ObjectCreation = "ObjectCreation";
    public const string ArrayCreation = "ArrayCreation";
    public const string DelegateCreation = "DelegateCreation";
    public const string Assignment = "Assignment";
    public const string MemberRead = "MemberRead";
    public const string MemberWrite = "MemberWrite";
    public const string ElementAccess = "ElementAccess";
    public const string PointerIndirection = "PointerIndirection";
    public const string Binary = "Binary";
    public const string Unary = "Unary";
    public const string Conversion = "Conversion";
    public const string Branch = "Branch";
    public const string Loop = "Loop";
    public const string Return = "Return";
    public const string Throw = "Throw";
    public const string Await = "Await";
    public const string Lock = "Lock";
    public const string InterpolatedString = "InterpolatedString";
}

/// <summary>Neutral read/write placement for member and element references.</summary>
public static class OperationAccessMode
{
    public const string Read = "Read";
    public const string Write = "Write";
}

/// <summary>Open input-role vocabulary.</summary>
public static class OperationInputRole
{
    public const string Receiver = "Receiver";
    public const string Argument = "Argument";
    public const string Target = "Target";
    public const string Value = "Value";
    public const string Left = "Left";
    public const string Right = "Right";
    public const string Operand = "Operand";
    public const string Condition = "Condition";
    public const string WhenTrue = "WhenTrue";
    public const string WhenFalse = "WhenFalse";
    public const string Index = "Index";
    public const string Collection = "Collection";
    public const string ReturnedValue = "ReturnedValue";
    public const string Exception = "Exception";
    public const string LockedValue = "LockedValue";
}

/// <summary>Open value-origin vocabulary.</summary>
public static class OperationValueKind
{
    public const string Literal = "Literal";
    public const string Null = "Null";
    public const string Constant = "Constant";
    public const string Field = "Field";
    public const string Property = "Property";
    public const string Parameter = "Parameter";
    public const string Local = "Local";
    public const string Invocation = "Invocation";
    public const string ObjectCreation = "ObjectCreation";
    public const string ArrayCreation = "ArrayCreation";
    public const string Binary = "Binary";
    public const string Unary = "Unary";
    public const string Conditional = "Conditional";
    public const string Default = "Default";
    public const string Lambda = "Lambda";
    public const string MethodGroup = "MethodGroup";
    public const string InterpolatedString = "InterpolatedString";
    public const string Other = "Other";
}

/// <summary>Open constant-provenance vocabulary.</summary>
public static class OperationConstantOrigin
{
    public const string Literal = "Literal";
    public const string NamedConstant = "NamedConstant";
    public const string DefaultValue = "DefaultValue";
    public const string FoldedExpression = "FoldedExpression";
}

/// <summary>Adapter-neutral numeric classification for constant values.</summary>
public static class OperationNumericClassification
{
    public const string NonNumeric = "NonNumeric";
    public const string Finite = "Finite";
    public const string NaN = "NaN";
    public const string PositiveInfinity = "PositiveInfinity";
    public const string NegativeInfinity = "NegativeInfinity";
}

/// <summary>Open control-context vocabulary.</summary>
public static class OperationControlContextKind
{
    public const string Branch = "Branch";
    public const string Loop = "Loop";
    public const string Switch = "Switch";
    public const string Try = "Try";
    public const string Catch = "Catch";
    public const string AnonymousFunction = "AnonymousFunction";
    public const string LocalFunction = "LocalFunction";
}

/// <summary>
/// Lexical arm occupied by an occurrence inside a branch context. Null on a
/// non-branch context. This is occurrence placement, not a control-flow or
/// runtime-reachability claim.
/// </summary>
public static class OperationBranchArm
{
    public const string Condition = "Condition";
    public const string WhenTrue = "WhenTrue";
    public const string WhenFalse = "WhenFalse";

    public static readonly string[] All = { Condition, WhenTrue, WhenFalse };
}
