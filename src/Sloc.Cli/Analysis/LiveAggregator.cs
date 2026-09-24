using Sloc.Core.Models;

namespace Sloc.Cli.Analysis;

/// <summary>
/// Thread-safe incremental aggregator of per-language counts, used by
/// <see cref="AnalyzeHandler"/> to refresh the live table without re-aggregating every
/// analyzed file on each tick. With <c>unique</c> (<c>--unique</c>), a file whose
/// <see cref="FileAnalysis.Hash"/> was already added is counted as processed but not
/// aggregated, so the running counts don't include duplicates the final summary drops.
/// </summary>
internal sealed class LiveAggregator(LanguageSort sortBy, int? top, bool unique = false)
{
    private readonly Dictionary<string, Counts> _byLanguage = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenHashes = [];
    private readonly object _gate = new();
    private int _processed;
    private int _files;

    public int FilesProcessed
    {
        get
        {
            lock (_gate)
            {
                return _processed;
            }
        }
    }

    public void Add(FileAnalysis analysis)
    {
        lock (_gate)
        {
            _processed++;
            if (unique && analysis.Hash is { } hash && !_seenHashes.Add(hash))
            {
                return;
            }

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

            // Order every language but leave the --top trim to the summary, so the totals
            // still cover all of them (as the final, per-file summary's totals do).
            var ordered = AnalysisSummary.OrderAndLimit(
                byLanguage,
                sortBy,
                descending: sortBy != LanguageSort.Name);

            return new AnalysisSummary(ordered, _files, top: top);
        }
    }

    private readonly record struct Counts(int Files, int Code, int Comment, int Blank, int ComplexityTotal = 0, bool HasComplexitySupport = false);
}