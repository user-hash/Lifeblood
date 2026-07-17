using Lifeblood.Domain.Results;

namespace Lifeblood.Application.Ports.Left;

/// <summary>
/// Format-adapter port for external runtime evidence. Implementations parse one
/// bounded document and return neutral records; they own neither file I/O nor
/// semantic correlation and retain no capture state between requests.
/// </summary>
public interface IPerformanceEvidenceImporter
{
    PerformanceCapture Import(PerformanceEvidenceDocument document);
}
