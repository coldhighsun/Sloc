using Sloc.Core.Models;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains tests for the per-language sorting and <c>top</c> limiting on
/// <see cref="AnalysisSummary"/>.
/// </summary>
public class AnalysisSummarySortTests
{
    /// <summary>
    /// Verifies that the default ordering is by total lines descending.
    /// </summary>
    [Fact]
    public void ByLanguage_DefaultsToTotalDescending()
    {
        var summary = new AnalysisSummary(Sample());

        Assert.Equal(["C#", "Python", "Go"], summary.ByLanguage.Select(s => s.Language).ToArray());
    }

    /// <summary>
    /// Verifies that ordering by comment lines descending reorders the languages.
    /// </summary>
    [Fact]
    public void ByLanguage_SortByComment_OrdersByCommentDescending()
    {
        var summary = new AnalysisSummary(Sample(), sortBy: LanguageSort.Comment);

        Assert.Equal("Go", summary.ByLanguage[0].Language);
    }

    /// <summary>
    /// Verifies that ordering by name is alphabetical ascending.
    /// </summary>
    [Fact]
    public void ByLanguage_SortByName_OrdersAlphabetically()
    {
        var summary = new AnalysisSummary(Sample(), sortBy: LanguageSort.Name, descending: false);

        Assert.Equal(["C#", "Go", "Python"], summary.ByLanguage.Select(s => s.Language).ToArray());
    }

    /// <summary>
    /// Verifies that <c>top</c> keeps only the requested number of languages.
    /// </summary>
    [Fact]
    public void ByLanguage_Top_LimitsRows()
    {
        var summary = new AnalysisSummary(Sample(), top: 2);

        Assert.Equal(2, summary.ByLanguage.Count);
        Assert.Equal(["C#", "Python"], summary.ByLanguage.Select(s => s.Language).ToArray());
    }

    private static IReadOnlyList<FileAnalysis> Sample() =>
    [
        new() { Path = "a.cs", Language = "C#", Code = 100, Comment = 5, Blank = 0 },
        new() { Path = "b.py", Language = "Python", Code = 50, Comment = 10, Blank = 0 },
        new() { Path = "c.go", Language = "Go", Code = 10, Comment = 30, Blank = 0 }
    ];
}
