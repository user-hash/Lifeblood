namespace Lifeblood.Domain.Results;

/// <summary>
/// Execution and diagnostic-location scope for a host compiler emit check.
/// Neither source placement nor emission establishes edit ownership or a
/// successful build by another compiler/toolchain.
/// </summary>
public sealed class CompilerValidationReceipt
{
    public string Kind { get; init; } = "HostCompilerEmit";
    public required IReadOnlyDictionary<string, string> Compiler { get; init; }
    public required string LanguageVersion { get; init; }
    public required bool CompleteModuleEmitSucceeded { get; init; }
    public bool TargetBuildValidated { get; init; }
    public required bool LocationlessBindingComparisonPerformed { get; init; }
    public required CompilationDiagnosticAttribution[] DiagnosticAttribution { get; init; }
    public string[] Limitations { get; init; } = Array.Empty<string>();
}

public sealed record CompilationDiagnosticAttribution(
    int DiagnosticIndex,
    string Scope,
    string Phase,
    bool HasSourceLocation);

public static class CompilerDiagnosticScope
{
    public const string RequestedSource = "RequestedSource";
    public const string OtherSource = "OtherSource";
    public const string Compilation = "Compilation";
}

public static class CompilerDiagnosticPhase
{
    public const string ReportedByEmit = "ReportedByEmit";
    public const string Binding = "Binding";
    public const string EmitOnly = "EmitOnly";
}
