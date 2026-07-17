using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Language-adapter port for bounded lexical evidence from the analyzed source
/// trees. Providers stream neutral facts and retain neither a second syntax
/// tree set nor a source-evidence cache.
/// </summary>
public interface ISourceEvidenceProvider
{
    SourceEvidenceScanReceipt ScanSourceEvidence(
        SourceEvidenceQuery query,
        Func<SourceEvidenceFact, bool> consume,
        CancellationToken cancellationToken = default);
}
