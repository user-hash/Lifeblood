using Lifeblood.Application.Ports.Left;

namespace Lifeblood.Adapters.CSharp.Internal;

/// <summary>
/// INV-MULTI-DEFINE-APPLIER-001. Pure transform from a baseline preprocessor
/// symbol set + a <see cref="DefineProfile"/> to the active symbol set for
/// the profile. Result is ordinal-sorted + distinct for byte-stable
/// provenance. <c>(BASE - RemoveDefines) ∪ AddDefines</c>.
/// </summary>
internal static class DefineProfileApplier
{
    public static string[] Apply(IReadOnlyList<string> baseSymbols, DefineProfile profile)
    {
        var active = new HashSet<string>(baseSymbols, StringComparer.Ordinal);
        foreach (var remove in profile.RemoveDefines) active.Remove(remove);
        foreach (var add in profile.AddDefines) active.Add(add);
        return active.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Returns a shallow clone of <paramref name="module"/> with the
    /// preprocessor-symbol set replaced by the active set for the profile.
    /// </summary>
    public static ModuleInfo WithProfileDefines(ModuleInfo module, DefineProfile profile)
    {
        var active = Apply(module.PreprocessorSymbols, profile);
        return new ModuleInfo
        {
            Name = module.Name,
            FilePaths = module.FilePaths,
            Dependencies = module.Dependencies,
            IsPure = module.IsPure,
            ExternalDllPaths = module.ExternalDllPaths,
            BclOwnership = module.BclOwnership,
            AllowUnsafeCode = module.AllowUnsafeCode,
            ImplicitUsings = module.ImplicitUsings,
            PreprocessorSymbols = active,
            LanguageVersion = module.LanguageVersion,
            NullableContext = module.NullableContext,
            NoWarnDiagnosticIds = module.NoWarnDiagnosticIds,
            CompilerFeatures = module.CompilerFeatures,
            ReferenceClosure = module.ReferenceClosure,
            InternalsVisibleTo = module.InternalsVisibleTo,
            Properties = module.Properties,
        };
    }
}
