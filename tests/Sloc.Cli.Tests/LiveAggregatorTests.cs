using Sloc.Cli.Analysis;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="LiveAggregator"/>.
/// </summary>
public class LiveAggregatorTests
{
    /// <summary>
    /// Verifies that adding files accumulates per-language counts and the total file count.
    /// </summary>
    [Fact]
    public void Add_MultipleFilesSameLanguage_AccumulatesCounts()
    {
        var aggregator = new LiveAggregator(LanguageSort.Total, top: null);

        aggregator.Add(new FileAnalysis { Language = "C#", Path = "a.cs", Code = 10, Comment = 2, Blank = 1 });
        aggregator.Add(new FileAnalysis { Language = "C#", Path = "b.cs", Code = 5, Comment = 1, Blank = 0 });

        Assert.Equal(2, aggregator.FilesProcessed);

        var summary = aggregator.ToSummary();
        var stats = Assert.Single(summary.ByLanguage);
        Assert.Equal("C#", stats.Language);
        Assert.Equal(2, stats.Files);
        Assert.Equal(15, stats.Code);
        Assert.Equal(3, stats.Comment);
        Assert.Equal(1, stats.Blank);
    }

    /// <summary>
    /// Verifies that files in different languages are aggregated into separate rows.
    /// </summary>
    [Fact]
    public void Add_DifferentLanguages_ProducesSeparateRows()
    {
        var aggregator = new LiveAggregator(LanguageSort.Name, top: null);

        aggregator.Add(new FileAnalysis { Language = "C#", Path = "a.cs", Code = 1, Comment = 0, Blank = 0 });
        aggregator.Add(new FileAnalysis { Language = "Python", Path = "a.py", Code = 2, Comment = 0, Blank = 0 });

        var summary = aggregator.ToSummary();

        Assert.Equal(2, summary.ByLanguage.Count);
        Assert.Contains(summary.ByLanguage, l => l.Language == "C#");
        Assert.Contains(summary.ByLanguage, l => l.Language == "Python");
    }

    /// <summary>
    /// Verifies that only files whose language supports complexity contribute to the
    /// language's complexity total, and languages with no complexity-supporting files
    /// report <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Add_ComplexityOnlyForSupportedLanguage_TotalsOnlyWhenPresent()
    {
        var aggregator = new LiveAggregator(LanguageSort.Total, top: null);

        aggregator.Add(new FileAnalysis { Language = "C#", Path = "a.cs", Code = 1, Comment = 0, Blank = 0, Complexity = 3 });
        aggregator.Add(new FileAnalysis { Language = "C#", Path = "b.cs", Code = 1, Comment = 0, Blank = 0, Complexity = 2 });
        aggregator.Add(new FileAnalysis { Language = "Text", Path = "c.txt", Code = 1, Comment = 0, Blank = 0, Complexity = null });

        var summary = aggregator.ToSummary();

        var csharp = summary.ByLanguage.Single(l => l.Language == "C#");
        Assert.Equal(5, csharp.ComplexityTotal);

        var text = summary.ByLanguage.Single(l => l.Language == "Text");
        Assert.Null(text.ComplexityTotal);
    }

    /// <summary>
    /// Verifies that <c>top</c> limits the number of rows returned, keeping the highest
    /// values under the requested sort.
    /// </summary>
    [Fact]
    public void ToSummary_TopLimitsRowCount()
    {
        var aggregator = new LiveAggregator(LanguageSort.Code, top: 1);

        aggregator.Add(new FileAnalysis { Language = "A", Path = "a", Code = 1, Comment = 0, Blank = 0 });
        aggregator.Add(new FileAnalysis { Language = "B", Path = "b", Code = 10, Comment = 0, Blank = 0 });

        var summary = aggregator.ToSummary();

        var stats = Assert.Single(summary.ByLanguage);
        Assert.Equal("B", stats.Language);
    }

    /// <summary>
    /// Verifies that <c>top</c> only trims the displayed rows: the overall totals still cover
    /// every language, matching the final (non-live) summary, whose totals are never
    /// top-limited either.
    /// </summary>
    [Fact]
    public void ToSummary_TopDoesNotTrimTotals()
    {
        var aggregator = new LiveAggregator(LanguageSort.Code, top: 1);

        aggregator.Add(new FileAnalysis { Language = "A", Path = "a", Code = 1, Comment = 2, Blank = 3, Complexity = 4 });
        aggregator.Add(new FileAnalysis { Language = "B", Path = "b", Code = 10, Comment = 0, Blank = 0, Complexity = 1 });

        var summary = aggregator.ToSummary();

        Assert.Single(summary.ByLanguage);
        Assert.Equal(2, summary.FileCount);
        Assert.Equal(11, summary.Code);
        Assert.Equal(2, summary.Comment);
        Assert.Equal(3, summary.Blank);
        Assert.Equal(5, summary.ComplexityTotal);
    }

    /// <summary>
    /// Verifies that with <c>unique</c>, a file whose content hash was already added counts
    /// toward progress but not toward the running totals, matching <c>--unique</c>'s final
    /// deduplication.
    /// </summary>
    [Fact]
    public void Add_UniqueSkipsDuplicateHashes()
    {
        var aggregator = new LiveAggregator(LanguageSort.Total, top: null, unique: true);

        aggregator.Add(new FileAnalysis { Language = "C#", Path = "a.cs", Code = 10, Comment = 0, Blank = 0, Hash = "H1" });
        aggregator.Add(new FileAnalysis { Language = "C#", Path = "copy.cs", Code = 10, Comment = 0, Blank = 0, Hash = "H1" });
        aggregator.Add(new FileAnalysis { Language = "C#", Path = "b.cs", Code = 5, Comment = 0, Blank = 0, Hash = "H2" });

        var summary = aggregator.ToSummary();

        Assert.Equal(3, aggregator.FilesProcessed);
        Assert.Equal(2, summary.FileCount);
        Assert.Equal(15, summary.Code);
    }

    /// <summary>
    /// Verifies that an aggregator with no added files reports zero files and no languages.
    /// </summary>
    [Fact]
    public void ToSummary_NoFilesAdded_ReturnsEmptySummary()
    {
        var aggregator = new LiveAggregator(LanguageSort.Total, top: null);

        var summary = aggregator.ToSummary();

        Assert.Equal(0, aggregator.FilesProcessed);
        Assert.Empty(summary.ByLanguage);
    }
}
