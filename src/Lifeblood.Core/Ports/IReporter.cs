using Lifeblood.Core.Analysis;

namespace Lifeblood.Core.Ports;

/// <summary>
/// Outputs analysis results in a specific format.
/// </summary>
public interface IReporter
{
    /// <summary>
    /// Write analysis results to the given output path.
    /// </summary>
    void Report(AnalysisResult result, string outputPath);

    /// <summary>Format identifier (e.g., "json", "html", "ci").</summary>
    string Format { get; }
}
