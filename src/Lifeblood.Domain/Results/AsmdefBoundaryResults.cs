using Lifeblood.Domain.Graph;

namespace Lifeblood.Domain.Results;

/// <summary>
/// Unity asmdef compile-direction report. DirectOnly source modules must
/// declare every module whose symbols their source edges reference.
/// INV-ASMDEF-CHECK-001.
/// </summary>
public sealed class AsmdefBoundaryReport
{
    public required int ModuleCount { get; init; }
    public required int DirectOnlyModuleCount { get; init; }
    public required int SkippedModuleCount { get; init; }
    public required int DeclaredModuleDependencyCount { get; init; }
    public required int CheckedCrossModuleEdgeCount { get; init; }
    public required int ViolationCount { get; init; }
    public required int ReturnedViolationCount { get; init; }
    public required bool Truncated { get; init; }
    public required bool Summarize { get; init; }
    public required int MaxResults { get; init; }
    public required bool ExcludeTests { get; init; }
    public required bool ExcludeGenerated { get; init; }
    public required AsmdefBoundaryViolation[] Violations { get; init; }
    public required string[] Limitations { get; init; }
}

/// <summary>One source-module to target-module missing-reference violation.</summary>
public sealed class AsmdefBoundaryViolation
{
    public required string SourceModuleId { get; init; }
    public required string SourceModuleName { get; init; }
    public required string TargetModuleId { get; init; }
    public required string TargetModuleName { get; init; }
    public required int OffendingEdgeCount { get; init; }
    public required string SourceSymbolId { get; init; }
    public required string TargetSymbolId { get; init; }
    public required string EdgeKind { get; init; }
    public CallSite? CallSite { get; init; }
    public IReadOnlyList<string>? Profiles { get; init; }
    public required string[] DeclaredDependencyModuleIds { get; init; }
}
