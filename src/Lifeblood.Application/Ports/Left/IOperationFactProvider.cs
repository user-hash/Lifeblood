using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Language-adapter port for bounded occurrence-level semantic facts. The
/// consumer returns false to stop the scan. Providers stream facts and retain
/// neither a second semantic graph nor compiler objects. INV-OPERATION-FACTS-001.
/// </summary>
public interface IOperationFactProvider
{
    OperationFactScanReceipt ScanOperationFacts(
        OperationFactQuery query,
        Func<OperationFact, bool> consume,
        CancellationToken cancellationToken = default);
}
