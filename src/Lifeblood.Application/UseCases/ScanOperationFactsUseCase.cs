using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Application-owned orchestration seam for one bounded operation-fact stream.
/// Rule engines consume facts through the callback without acquiring an adapter
/// dependency or retaining a second semantic base. INV-CONTRACT-AUDIT-001.
/// </summary>
public sealed class ScanOperationFactsUseCase
{
    private readonly IOperationFactProvider _provider;

    public ScanOperationFactsUseCase(IOperationFactProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public OperationFactScanReceipt Execute(
        OperationFactQuery query,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        if (query.MaxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "maxFacts must be greater than zero.");

        return _provider.ScanOperationFacts(query, consume, cancellationToken);
    }
}
