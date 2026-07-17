using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

/// <summary>
/// Application-owned orchestration seam for one bounded lexical-evidence
/// stream. Policy evaluation remains above the language adapter.
/// </summary>
public sealed class ScanSourceEvidenceUseCase
{
    private readonly ISourceEvidenceProvider _provider;

    public ScanSourceEvidenceUseCase(ISourceEvidenceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public SourceEvidenceScanReceipt Execute(
        SourceEvidenceQuery query,
        Func<SourceEvidenceFact, bool> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(consume);
        if (query.MaxFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "maxFacts must be greater than zero.");
        if (query.SearchTerms is not { Count: > 0 })
            throw new ArgumentException("At least one source-evidence search term is required.", nameof(query));

        return _provider.ScanSourceEvidence(query, consume, cancellationToken);
    }
}
