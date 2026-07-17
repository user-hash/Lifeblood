using Lifeblood.Application.Ports.Left;
using Lifeblood.Domain.Results;

namespace Lifeblood.Application.UseCases;

public sealed class ImportPerformanceEvidenceUseCase
{
    public const int HardMaximumMeasurements = 100_000;

    private readonly IPerformanceEvidenceImporter _importer;

    public ImportPerformanceEvidenceUseCase(IPerformanceEvidenceImporter importer)
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    }

    public PerformanceCapture Execute(PerformanceEvidenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.SourceName))
            throw new ArgumentException("A runtime-evidence source name is required.", nameof(document));
        if (string.IsNullOrWhiteSpace(document.Content))
            throw new ArgumentException("Runtime-evidence content cannot be empty.", nameof(document));
        if (document.MaximumMeasurements is <= 0 or > HardMaximumMeasurements)
        {
            throw new ArgumentOutOfRangeException(
                nameof(document),
                $"MaximumMeasurements must be between 1 and {HardMaximumMeasurements}.");
        }

        return _importer.Import(document);
    }
}
