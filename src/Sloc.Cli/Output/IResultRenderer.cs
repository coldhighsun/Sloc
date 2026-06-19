using Sloc.Core.Models;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders an <see cref="AnalysisSummary"/> to the console.
/// </summary>
public interface IResultRenderer
{
    /// <summary>
    /// Renders the supplied summary.
    /// </summary>
    /// <param name="summary">The summary to render.</param>
    /// <param name="byFile">Whether to include a per-file breakdown.</param>
    /// <param name="noHealth">When <see langword="true"/>, the Comment Health column and percentage breakdowns are hidden.</param>
    void Render(AnalysisSummary summary, bool byFile, bool noHealth);
}