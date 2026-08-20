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
    /// <param name="detailed">
    /// When <see langword="true"/>, emit both the by-language summary and the per-file
    /// breakdown together instead of one or the other.
    /// </param>
    /// <param name="sourcePath">
    /// The path, list file, or git commit/tree-ish that was analyzed, included in report
    /// metadata (where supported) so a saved or shared report can be traced back to its
    /// source. <see langword="null"/> omits it.
    /// </param>
    void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false, string? sourcePath = null);
}