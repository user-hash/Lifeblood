using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>Validates inert consumer policy before any fact scan begins.</summary>
internal static class ContractManifestValidator
{
    internal static void Validate(ContractManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, ContractManifest.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Unsupported contract manifest schema '{manifest.SchemaVersion}'. Expected '{ContractManifest.CurrentSchemaVersion}'.");
        RequireText(manifest.Id, "Manifest id");
        RequireText(manifest.Version, "Manifest version");

        var guards = manifest.OperationGuards ?? throw new ArgumentException("operationGuards cannot be null.");
        var costs = manifest.ExternalApiCosts ?? throw new ArgumentException("externalApiCosts cannot be null.");
        var domains = manifest.ValueDomains ?? throw new ArgumentException("valueDomains cannot be null.");
        var suppressions = manifest.Suppressions ?? throw new ArgumentException("suppressions cannot be null.");
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contract in guards)
            ValidateGuard(contract, ids);
        foreach (var contract in costs)
            ValidateCost(contract, ids);
        foreach (var contract in domains)
            ValidateValueDomain(contract, ids);
        foreach (var suppression in suppressions)
            ValidateSuppression(suppression);
    }

    private static void ValidateGuard(OperationGuardContract contract, HashSet<string> ids)
    {
        ValidateContractIdentity(contract.Id, ids);
        RequireValues(contract.TargetSymbolIds, $"Operation guard '{contract.Id}' targetSymbolIds");
        RequireNonNull(contract.AllowedValueKinds, $"Operation guard '{contract.Id}' allowedValueKinds");
        RequireNonNull(contract.AllowedSourceSymbolIds, $"Operation guard '{contract.Id}' allowedSourceSymbolIds");
        RequireNonNull(contract.AllowedControlContextKinds, $"Operation guard '{contract.Id}' allowedControlContextKinds");
        RequireNonNull(contract.AllowedControlOperators, $"Operation guard '{contract.Id}' allowedControlOperators");
        if (contract.ArgumentOrdinal < 0)
            throw new ArgumentException($"Operation guard '{contract.Id}' argumentOrdinal cannot be negative.");
        RequireText(contract.Severity, $"Operation guard '{contract.Id}' severity");
    }

    private static void ValidateCost(ExternalApiCostContract contract, HashSet<string> ids)
    {
        ValidateContractIdentity(contract.Id, ids);
        RequireValues(contract.TargetSymbolIds, $"External API cost '{contract.Id}' targetSymbolIds");
        RequireValues(contract.OperationKinds, $"External API cost '{contract.Id}' operationKinds");
        RequireValues(contract.Categories, $"External API cost '{contract.Id}' categories");
        RequireNonNull(contract.ControlContextKinds, $"External API cost '{contract.Id}' controlContextKinds");
        RequireNonNull(contract.ContainingSymbolIds, $"External API cost '{contract.Id}' containingSymbolIds");
        RequireText(contract.AnnotationSource, $"External API cost '{contract.Id}' annotationSource");
        RequireText(contract.Severity, $"External API cost '{contract.Id}' severity");
        if (!contract.ReportEveryOccurrence
            && contract.ControlContextKinds.Length == 0
            && contract.ContainingSymbolIds.Length == 0)
        {
            throw new ArgumentException(
                $"External API cost '{contract.Id}' must report every occurrence or select a control/containing-symbol context.");
        }
    }

    private static void ValidateValueDomain(ValueDomainContract contract, HashSet<string> ids)
    {
        ValidateContractIdentity(contract.Id, ids);
        RequireValues(contract.TargetSymbolIds, $"Value domain '{contract.Id}' targetSymbolIds");
        RequireValues(contract.OperationKinds, $"Value domain '{contract.Id}' operationKinds");
        RequireText(contract.InputRole, $"Value domain '{contract.Id}' inputRole");
        if (contract.InputOrdinal < 0)
            throw new ArgumentException($"Value domain '{contract.Id}' inputOrdinal cannot be negative.");
        RequireText(contract.TargetDomain, $"Value domain '{contract.Id}' targetDomain");
        RequireText(contract.Severity, $"Value domain '{contract.Id}' severity");

        var bindings = contract.Bindings
            ?? throw new ArgumentException($"Value domain '{contract.Id}' bindings cannot be null.");
        if (bindings.Length == 0)
            throw new ArgumentException($"Value domain '{contract.Id}' bindings must contain at least one entry.");
        var declaredDomains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            RequireText(binding.Domain, $"Value domain '{contract.Id}' binding domain");
            if (!declaredDomains.Add(binding.Domain))
                throw new ArgumentException($"Value domain '{contract.Id}' has duplicate binding domain '{binding.Domain}'.");
            RequireValues(
                binding.SourceSymbolIds,
                $"Value domain '{contract.Id}' binding '{binding.Domain}' sourceSymbolIds");
        }

        var conversions = contract.AllowedConversions
            ?? throw new ArgumentException($"Value domain '{contract.Id}' allowedConversions cannot be null.");
        var conversionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var conversion in conversions)
        {
            RequireText(conversion.Id, $"Value domain '{contract.Id}' conversion id");
            if (!conversionIds.Add(conversion.Id))
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' has duplicate conversion id '{conversion.Id}'.");
            RequireValues(
                conversion.SourceDomains,
                $"Value domain '{contract.Id}' conversion '{conversion.Id}' sourceDomains");
            RequireNonNull(
                conversion.RequiredSourceSymbolIds,
                $"Value domain '{contract.Id}' conversion '{conversion.Id}' requiredSourceSymbolIds");
            RequireNonNull(
                conversion.RequiredOperators,
                $"Value domain '{contract.Id}' conversion '{conversion.Id}' requiredOperators");
            foreach (var sourceDomain in conversion.SourceDomains)
            {
                if (!declaredDomains.Contains(sourceDomain))
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' conversion '{conversion.Id}' references " +
                        $"undeclared source domain '{sourceDomain}'.");
                }
            }
            if (conversion.RequiredSourceSymbolIds.Length == 0
                && conversion.RequiredOperators.Length == 0)
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' conversion '{conversion.Id}' must require at least one " +
                    "source symbol or operator as conversion evidence.");
            }
        }
    }

    private static void ValidateSuppression(ContractSuppression suppression)
    {
        RequireText(suppression.Id, "Suppression id");
        RequireText(suppression.Reason, $"Suppression '{suppression.Id}' reason");
        RequireNonNull(suppression.RuleIds, $"Suppression '{suppression.Id}' ruleIds");
        RequireNonNull(suppression.ContractIds, $"Suppression '{suppression.Id}' contractIds");
        RequireNonNull(suppression.FactIds, $"Suppression '{suppression.Id}' factIds");
        RequireNonNull(suppression.ContainingSymbolIds, $"Suppression '{suppression.Id}' containingSymbolIds");
        RequireNonNull(suppression.FilePaths, $"Suppression '{suppression.Id}' filePaths");
        if (suppression.RuleIds.Length == 0
            && suppression.ContractIds.Length == 0
            && suppression.FactIds.Length == 0
            && suppression.ContainingSymbolIds.Length == 0
            && suppression.FilePaths.Length == 0)
        {
            throw new ArgumentException($"Suppression '{suppression.Id}' must declare at least one selector.");
        }
    }

    private static void ValidateContractIdentity(string id, HashSet<string> ids)
    {
        RequireText(id, "Contract id");
        if (!ids.Add(id))
            throw new ArgumentException($"Duplicate contract id '{id}'.");
    }

    private static void RequireText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(label + " is required.");
    }

    private static void RequireValues(string[] values, string label)
    {
        if (values == null || values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(label + " must contain non-empty values.");
    }

    private static void RequireNonNull(string[] values, string label)
    {
        if (values == null)
            throw new ArgumentException(label + " cannot be null.");
    }
}
