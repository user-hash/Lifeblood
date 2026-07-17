using Lifeblood.Domain.Results;

namespace Lifeblood.Analysis;

/// <summary>Validates inert consumer policy before any fact scan begins.</summary>
public static class ContractManifestValidator
{
    public static void Validate(ContractManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, ContractManifest.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Unsupported contract manifest schema '{manifest.SchemaVersion}'. Expected '{ContractManifest.CurrentSchemaVersion}'.");
        RequireText(manifest.Id, "Manifest id");
        RequireText(manifest.Version, "Manifest version");

        var routes = manifest.CallRoutes ?? throw new ArgumentException("callRoutes cannot be null.");
        var guards = manifest.OperationGuards ?? throw new ArgumentException("operationGuards cannot be null.");
        var costs = manifest.ExternalApiCosts ?? throw new ArgumentException("externalApiCosts cannot be null.");
        var domains = manifest.ValueDomains ?? throw new ArgumentException("valueDomains cannot be null.");
        var shapes = manifest.OperationShapes ?? throw new ArgumentException("operationShapes cannot be null.");
        var suppressions = manifest.Suppressions ?? throw new ArgumentException("suppressions cannot be null.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var routeIds = new HashSet<string>(StringComparer.Ordinal);

        if (routes.Length > 32)
            throw new ArgumentException("callRoutes cannot contain more than 32 entries.");
        foreach (var route in routes)
            ValidateCallRoute(route, routeIds);

        foreach (var contract in guards)
            ValidateGuard(contract, ids);
        foreach (var contract in costs)
            ValidateCost(contract, ids, routeIds);
        foreach (var contract in domains)
            ValidateValueDomain(contract, ids);
        foreach (var contract in shapes)
            ValidateOperationShape(contract, ids);
        foreach (var suppression in suppressions)
            ValidateSuppression(suppression);
    }

    private static void ValidateCallRoute(ContractCallRoute route, HashSet<string> routeIds)
    {
        RequireText(route.Id, "Call route id");
        if (!routeIds.Add(route.Id))
            throw new ArgumentException($"Duplicate call route id '{route.Id}'.");
        RequireValues(route.RootSymbolIds, $"Call route '{route.Id}' rootSymbolIds");
        if (route.RootSymbolIds.Length > 32)
            throw new ArgumentException($"Call route '{route.Id}' cannot contain more than 32 roots.");
        if (route.RootSymbolIds.Distinct(StringComparer.Ordinal).Count() != route.RootSymbolIds.Length)
            throw new ArgumentException($"Call route '{route.Id}' rootSymbolIds cannot contain duplicates.");
        if (route.MaxDepth < 0 || route.MaxDepth > ContractCallRoute.MaximumDepth)
        {
            throw new ArgumentException(
                $"Call route '{route.Id}' maxDepth must be between 0 and {ContractCallRoute.MaximumDepth}.");
        }
        if (route.MaxMembers < 1 || route.MaxMembers > ContractCallRoute.MaximumMembers)
        {
            throw new ArgumentException(
                $"Call route '{route.Id}' maxMembers must be between 1 and {ContractCallRoute.MaximumMembers}.");
        }
        if (route.MaxMembers < route.RootSymbolIds.Length)
        {
            throw new ArgumentException(
                $"Call route '{route.Id}' maxMembers must retain all {route.RootSymbolIds.Length} declared roots.");
        }
    }

    private static void ValidateOperationShape(OperationShapeContract contract, HashSet<string> ids)
    {
        ValidateContractIdentity(contract.Id, ids);
        RequireValues(contract.OperationKinds, $"Operation shape '{contract.Id}' operationKinds");
        RequireNonNull(contract.TargetSymbolIds, $"Operation shape '{contract.Id}' targetSymbolIds");
        RequireNonNull(contract.ContainingSymbolIds, $"Operation shape '{contract.Id}' containingSymbolIds");
        RequireNonNull(contract.Operators, $"Operation shape '{contract.Id}' operators");
        RequireNonNull(contract.Categories, $"Operation shape '{contract.Id}' categories");
        RequireNoBlankValues(contract.TargetSymbolIds, $"Operation shape '{contract.Id}' targetSymbolIds");
        RequireNoBlankValues(contract.ContainingSymbolIds, $"Operation shape '{contract.Id}' containingSymbolIds");
        RequireNoBlankValues(contract.Operators, $"Operation shape '{contract.Id}' operators");
        RequireNoBlankValues(contract.Categories, $"Operation shape '{contract.Id}' categories");
        RequireText(contract.Severity, $"Operation shape '{contract.Id}' severity");
        if (contract.UniquenessPolicy is { } uniqueness)
        {
            RequireText(uniqueness.InputRole, $"Operation shape '{contract.Id}' uniquenessPolicy inputRole");
            if (uniqueness.InputOrdinal < 0)
                throw new ArgumentException($"Operation shape '{contract.Id}' uniquenessPolicy inputOrdinal cannot be negative.");
            RequireText(uniqueness.KeyKind, $"Operation shape '{contract.Id}' uniquenessPolicy keyKind");
            if (!OperationShapeKeyKind.All.Contains(uniqueness.KeyKind, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Operation shape '{contract.Id}' has unknown uniquenessPolicy keyKind '{uniqueness.KeyKind}'.");
            }
            if (uniqueness.MinimumOccurrences < 2)
            {
                throw new ArgumentException(
                    $"Operation shape '{contract.Id}' uniquenessPolicy minimumOccurrences must be at least 2.");
            }
        }

        var shapes = contract.AllowedShapes
            ?? throw new ArgumentException($"Operation shape '{contract.Id}' allowedShapes cannot be null.");
        if (shapes.Length == 0)
            throw new ArgumentException($"Operation shape '{contract.Id}' allowedShapes must contain at least one entry.");

        var shapeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in shapes)
        {
            if (shape == null)
                throw new ArgumentException($"Operation shape '{contract.Id}' allowedShapes cannot contain null entries.");
            RequireText(shape.Id, $"Operation shape '{contract.Id}' allowed shape id");
            if (!shapeIds.Add(shape.Id))
                throw new ArgumentException($"Operation shape '{contract.Id}' has duplicate allowed shape id '{shape.Id}'.");

            var inputs = shape.Inputs
                ?? throw new ArgumentException($"Operation shape '{contract.Id}' shape '{shape.Id}' inputs cannot be null.");
            var controls = shape.ControlContexts
                ?? throw new ArgumentException($"Operation shape '{contract.Id}' shape '{shape.Id}' controlContexts cannot be null.");
            RequireNonNull(
                shape.AllowedResultTypes,
                $"Operation shape '{contract.Id}' shape '{shape.Id}' allowedResultTypes");
            RequireNoBlankValues(
                shape.AllowedResultTypes,
                $"Operation shape '{contract.Id}' shape '{shape.Id}' allowedResultTypes");
            if (inputs.Length == 0 && controls.Length == 0 && shape.AllowedResultTypes.Length == 0)
            {
                throw new ArgumentException(
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' must constrain an input, result type, or control context.");
            }

            var inputSelectors = new HashSet<(string Role, int? Ordinal)>();
            foreach (var input in inputs)
            {
                if (input == null)
                    throw new ArgumentException($"Operation shape '{contract.Id}' shape '{shape.Id}' inputs cannot contain null entries.");
                RequireText(input.Role, $"Operation shape '{contract.Id}' shape '{shape.Id}' input role");
                if (input.Ordinal < 0)
                    throw new ArgumentException(
                        $"Operation shape '{contract.Id}' shape '{shape.Id}' input ordinal cannot be negative.");
                if (!inputSelectors.Add((input.Role, input.Ordinal)))
                {
                    throw new ArgumentException(
                        $"Operation shape '{contract.Id}' shape '{shape.Id}' repeats input selector " +
                        $"'{input.Role}' ordinal '{input.Ordinal?.ToString() ?? "any"}'.");
                }
                ValidateInputShape(contract.Id, shape.Id, input);
            }

            foreach (var control in controls)
            {
                if (control == null)
                {
                    throw new ArgumentException(
                        $"Operation shape '{contract.Id}' shape '{shape.Id}' controlContexts cannot contain null entries.");
                }
                RequireText(control.Kind, $"Operation shape '{contract.Id}' shape '{shape.Id}' control kind");
                RequireNonNull(
                    control.AllowedBranchArms,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control allowedBranchArms");
                RequireNonNull(
                    control.AnySourceSymbolIds,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control anySourceSymbolIds");
                RequireNonNull(
                    control.RequiredSourceSymbolIds,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control requiredSourceSymbolIds");
                RequireNonNull(
                    control.RequiredOperators,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control requiredOperators");
                RequireNoBlankValues(
                    control.AllowedBranchArms,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control allowedBranchArms");
                RequireNoBlankValues(
                    control.AnySourceSymbolIds,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control anySourceSymbolIds");
                RequireNoBlankValues(
                    control.RequiredSourceSymbolIds,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control requiredSourceSymbolIds");
                RequireNoBlankValues(
                    control.RequiredOperators,
                    $"Operation shape '{contract.Id}' shape '{shape.Id}' control requiredOperators");
                if (control.AllowedBranchArms.Length > 0
                    && !string.Equals(control.Kind, OperationControlContextKind.Branch, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Operation shape '{contract.Id}' shape '{shape.Id}' can select branch arms only on a Branch control context.");
                }
                var unknownArms = control.AllowedBranchArms
                    .Where(arm => !OperationBranchArm.All.Contains(arm, StringComparer.Ordinal))
                    .ToArray();
                if (unknownArms.Length > 0)
                {
                    throw new ArgumentException(
                        $"Operation shape '{contract.Id}' shape '{shape.Id}' has unknown branch arms " +
                        $"[{string.Join(", ", unknownArms)}].");
                }
            }
        }
    }

    private static void ValidateInputShape(
        string contractId,
        string shapeId,
        OperationInputShape input)
    {
        var prefix = $"Operation shape '{contractId}' shape '{shapeId}' input '{input.Role}'";
        RequireNonNull(input.AllowedValueKinds, prefix + " allowedValueKinds");
        RequireNonNull(input.AllowedTypes, prefix + " allowedTypes");
        RequireNonNull(input.AnySourceSymbolIds, prefix + " anySourceSymbolIds");
        RequireNonNull(input.RequiredSourceSymbolIds, prefix + " requiredSourceSymbolIds");
        RequireNonNull(input.RequiredOperators, prefix + " requiredOperators");
        RequireNonNull(input.AllowedConstantValues, prefix + " allowedConstantValues");
        RequireNonNull(input.RequiredConstantValues, prefix + " requiredConstantValues");
        RequireNonNull(input.ForbiddenConstantValues, prefix + " forbiddenConstantValues");
        RequireNoBlankValues(input.AllowedValueKinds, prefix + " allowedValueKinds");
        RequireNoBlankValues(input.AllowedTypes, prefix + " allowedTypes");
        RequireNoBlankValues(input.AnySourceSymbolIds, prefix + " anySourceSymbolIds");
        RequireNoBlankValues(input.RequiredSourceSymbolIds, prefix + " requiredSourceSymbolIds");
        RequireNoBlankValues(input.RequiredOperators, prefix + " requiredOperators");
        RequireNoBlankValues(input.AllowedConstantValues, prefix + " allowedConstantValues");
        RequireNoBlankValues(input.RequiredConstantValues, prefix + " requiredConstantValues");
        RequireNoBlankValues(input.ForbiddenConstantValues, prefix + " forbiddenConstantValues");
        var contradictoryConstants = input.ForbiddenConstantValues
            .Where(value => input.AllowedConstantValues.Contains(value, StringComparer.Ordinal)
                || input.RequiredConstantValues.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (contradictoryConstants.Length > 0)
        {
            throw new ArgumentException(
                prefix + " both accepts and forbids constants " +
                $"[{string.Join(", ", contradictoryConstants)}].");
        }
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

    private static void ValidateCost(
        ExternalApiCostContract contract,
        HashSet<string> ids,
        HashSet<string> routeIds)
    {
        ValidateContractIdentity(contract.Id, ids);
        RequireNonNull(contract.TargetSymbolIds, $"External API cost '{contract.Id}' targetSymbolIds");
        RequireNoBlankValues(contract.TargetSymbolIds, $"External API cost '{contract.Id}' targetSymbolIds");
        if (contract.MatchAnyTarget == (contract.TargetSymbolIds.Length > 0))
        {
            throw new ArgumentException(
                $"External API cost '{contract.Id}' must declare exactly one of matchAnyTarget:true or targetSymbolIds.");
        }
        RequireValues(contract.OperationKinds, $"External API cost '{contract.Id}' operationKinds");
        RequireValues(contract.Categories, $"External API cost '{contract.Id}' categories");
        RequireNonNull(contract.ControlContextKinds, $"External API cost '{contract.Id}' controlContextKinds");
        RequireNonNull(contract.ContainingSymbolIds, $"External API cost '{contract.Id}' containingSymbolIds");
        RequireNonNull(contract.CallRouteIds, $"External API cost '{contract.Id}' callRouteIds");
        RequireNoBlankValues(contract.CallRouteIds, $"External API cost '{contract.Id}' callRouteIds");
        var unknownRoutes = contract.CallRouteIds
            .Where(id => !routeIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknownRoutes.Length > 0)
        {
            throw new ArgumentException(
                $"External API cost '{contract.Id}' references unknown call routes " +
                $"[{string.Join(", ", unknownRoutes)}].");
        }
        RequireText(contract.AnnotationSource, $"External API cost '{contract.Id}' annotationSource");
        RequireText(contract.Severity, $"External API cost '{contract.Id}' severity");
        if (!contract.ReportEveryOccurrence
            && contract.ControlContextKinds.Length == 0
            && contract.ContainingSymbolIds.Length == 0
            && contract.CallRouteIds.Length == 0)
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

        if (contract.NonFinitePolicy is { } nonFinite)
        {
            RequireText(nonFinite.Action, $"Value domain '{contract.Id}' nonFinitePolicy action");
            if (!NonFinitePolicyAction.All.Contains(nonFinite.Action, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' has unknown nonFinitePolicy action '{nonFinite.Action}'.");
            }
            RequireNonNull(
                nonFinite.EvidenceSymbolIds,
                $"Value domain '{contract.Id}' nonFinitePolicy evidenceSymbolIds");
            if (nonFinite.EvidenceSymbolIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' nonFinitePolicy evidenceSymbolIds must contain non-empty values.");
            }
            if (nonFinite.RequireEvidenceForAllValues
                && string.Equals(nonFinite.Action, NonFinitePolicyAction.Allow, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' cannot require non-finite policy evidence when action is Allow.");
            }
            if (nonFinite.RequireEvidenceForAllValues && nonFinite.EvidenceSymbolIds.Length == 0)
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' requires non-finite policy evidence but declares no evidenceSymbolIds.");
            }
        }

        if (contract.ConstantPolicy is { } constants)
        {
            RequireNonNull(
                constants.AllowedLiteralValues,
                $"Value domain '{contract.Id}' constantPolicy allowedLiteralValues");
            if (constants.AllowedLiteralValues.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' constantPolicy allowedLiteralValues must contain non-empty values.");
            }
            if (!constants.ReportRawNumericLiterals && constants.AllowedLiteralValues.Length > 0)
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' cannot allow literal exceptions when raw-literal reporting is disabled.");
            }
            if (constants.NearEqualPolicy is { } nearEqual)
            {
                if (!double.IsFinite(nearEqual.AbsoluteTolerance)
                    || !double.IsFinite(nearEqual.RelativeTolerance)
                    || nearEqual.AbsoluteTolerance < 0d
                    || nearEqual.RelativeTolerance < 0d
                    || (nearEqual.AbsoluteTolerance == 0d && nearEqual.RelativeTolerance == 0d))
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' nearEqualPolicy requires a finite positive " +
                        "absoluteTolerance or relativeTolerance.");
                }
                if (nearEqual.MinimumOccurrences < 2)
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' nearEqualPolicy minimumOccurrences must be at least 2.");
                }
            }
        }

        if (contract.BoundaryPolicy is { } boundary)
        {
            RequireValues(boundary.ContextKinds, $"Value domain '{contract.Id}' boundaryPolicy contextKinds");
            RequireValues(
                boundary.BoundarySourceSymbolIds,
                $"Value domain '{contract.Id}' boundaryPolicy boundarySourceSymbolIds");
            var shapes = boundary.AllowedShapes
                ?? throw new ArgumentException(
                    $"Value domain '{contract.Id}' boundaryPolicy allowedShapes cannot be null.");
            if (shapes.Length == 0)
            {
                throw new ArgumentException(
                    $"Value domain '{contract.Id}' boundaryPolicy allowedShapes must contain at least one entry.");
            }
            foreach (var shape in shapes)
            {
                if (shape == null)
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' boundaryPolicy allowedShapes cannot contain null entries.");
                }
                RequireText(
                    shape.ComparisonOperator,
                    $"Value domain '{contract.Id}' boundaryPolicy comparisonOperator");
                RequireText(shape.BoundarySide, $"Value domain '{contract.Id}' boundaryPolicy boundarySide");
                if (!BoundaryOperandSide.All.Contains(shape.BoundarySide, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' boundaryPolicy has unknown boundarySide '{shape.BoundarySide}'.");
                }
                RequireNonNull(
                    shape.BoundaryValueKinds,
                    $"Value domain '{contract.Id}' boundaryPolicy boundaryValueKinds");
                RequireNonNull(
                    shape.BoundaryOperators,
                    $"Value domain '{contract.Id}' boundaryPolicy boundaryOperators");
                RequireNonNull(
                    shape.BoundaryConstantValues,
                    $"Value domain '{contract.Id}' boundaryPolicy boundaryConstantValues");
                if (shape.BoundaryValueKinds.Any(string.IsNullOrWhiteSpace)
                    || shape.BoundaryOperators.Any(string.IsNullOrWhiteSpace)
                    || shape.BoundaryConstantValues.Any(string.IsNullOrWhiteSpace))
                {
                    throw new ArgumentException(
                        $"Value domain '{contract.Id}' boundaryPolicy shape values must be non-empty.");
                }
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

    private static void RequireNoBlankValues(string[] values, string label)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(label + " must contain only non-empty values.");
    }
}
