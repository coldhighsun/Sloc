using Sloc.Core.Models;

namespace Sloc.Cli.Analysis;

/// <summary>
/// Thread-safe incremental aggregator of per-language counts, used by
/// <see cref="AnalyzeHandler"/> to refresh the live table without re-aggregating every
/// analyzed file on each tick.
/// </summary>
internal sealed class LiveAggregator(LanguageSort sortBy, int? top)
{
    private readonly Dictionary<string, Counts> _byLanguage = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _files;

    public int FilesProcessed
    {
        get
        {
            lock (_gate)
            {
                return _files;
            }
        }
    }

    public void Add(FileAnalysis analysis)
    {
        lock (_gate)
        {
            _files++;
            _byLanguage.TryGetValue(analysis.Language, out var counts);
            _byLanguage[analysis.Language] = new Counts(
                counts.Files + 1,
                counts.Code + analysis.Code,
                counts.Comment + analysis.Comment,
                counts.Blank + analysis.Blank,
                analysis.Complexity is { } complexity ? counts.ComplexityTotal + complexity : counts.ComplexityTotal,
                counts.HasComplexitySupport || analysis.Complexity.HasValue);
        }
    }

    public AnalysisSummary ToSummary()
    {
        lock (_gate)
        {
            var byLanguage = _byLanguage
                .Select(entry => new LanguageStatistics
                {
                    Language = entry.Key,
                    Files = entry.Value.Files,
                    Code = entry.Value.Code,
                    Comment = entry.Value.Comment,
                    Blank = entry.Value.Blank,
                    ComplexityTotal = entry.Value.HasComplexitySupport ? entry.Value.ComplexityTotal : null
                });

            var ordered = AnalysisSummary.OrderAndLimit(
                byLanguage,
                sortBy,
                descending: sortBy != LanguageSort.Name,
                top);

            return new AnalysisSummary(ordered, _files);
        }
    }

    private readonly record struct Counts(int Files, int Code, int Comment, int Blank, int ComplexityTotal = 0, bool HasComplexitySupport = false);
}