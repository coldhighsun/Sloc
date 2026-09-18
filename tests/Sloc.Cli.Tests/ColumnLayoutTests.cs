using Sloc.Cli.Output;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="ColumnLayout"/>.
/// </summary>
public class ColumnLayoutTests
{
    /// <summary>
    /// Verifies that the per-file header includes both optional columns when neither is
    /// suppressed.
    /// </summary>
    [Fact]
    public void FileHeader_BothColumnsEnabled_IncludesHealthAndComplexity()
    {
        var header = ColumnLayout.FileHeader(noHealth: false, noComplexity: false);

        Assert.Equal(["Path", "Language", "Code", "Comment", "Blank", "Total", "Health", "Complexity"], header);
    }

    /// <summary>
    /// Verifies that suppressing health and/or complexity omits the corresponding column
    /// from the per-file header.
    /// </summary>
    [Theory]
    [InlineData(true, false, new[] { "Path", "Language", "Code", "Comment", "Blank", "Total", "Complexity" })]
    [InlineData(false, true, new[] { "Path", "Language", "Code", "Comment", "Blank", "Total", "Health" })]
    [InlineData(true, true, new[] { "Path", "Language", "Code", "Comment", "Blank", "Total" })]
    public void FileHeader_SuppressedColumns_AreOmitted(bool noHealth, bool noComplexity, string[] expected)
    {
        var header = ColumnLayout.FileHeader(noHealth, noComplexity);

        Assert.Equal(expected, header);
    }

    /// <summary>
    /// Verifies that the per-language header uses "Files" instead of "Path" and includes
    /// both optional columns when neither is suppressed.
    /// </summary>
    [Fact]
    public void LanguageHeader_BothColumnsEnabled_IncludesHealthAndComplexity()
    {
        var header = ColumnLayout.LanguageHeader(noHealth: false, noComplexity: false);

        Assert.Equal(["Language", "Files", "Code", "Comment", "Blank", "Total", "Health", "Complexity"], header);
    }

    /// <summary>
    /// Verifies that custom column labels are used in place of the defaults.
    /// </summary>
    [Fact]
    public void LanguageHeader_CustomLabels_UsesSuppliedLabels()
    {
        var header = ColumnLayout.LanguageHeader(noHealth: false, noComplexity: false, healthLabel: "H", complexityLabel: "C");

        Assert.Equal(["Language", "Files", "Code", "Comment", "Blank", "Total", "H", "C"], header);
    }

    /// <summary>
    /// Verifies that <see cref="ColumnLayout.AddOptional"/> appends cells matching the
    /// toggles used to build the header.
    /// </summary>
    [Fact]
    public void AddOptional_BothEnabled_AppendsBothCells()
    {
        var row = new List<string> { "base" };

        ColumnLayout.AddOptional(row, noHealth: false, noComplexity: false, healthCell: "Good", complexityCell: "3");

        Assert.Equal(["base", "Good", "3"], row);
    }

    /// <summary>
    /// Verifies that <see cref="ColumnLayout.AddOptional"/> skips a cell whose column is
    /// suppressed.
    /// </summary>
    [Fact]
    public void AddOptional_ComplexitySuppressed_OmitsComplexityCell()
    {
        var row = new List<string> { "base" };

        ColumnLayout.AddOptional(row, noHealth: false, noComplexity: true, healthCell: "Good", complexityCell: "3");

        Assert.Equal(["base", "Good"], row);
    }

    /// <summary>
    /// Verifies that <see cref="CommentHealthLevel.NotApplicable"/> renders as an empty
    /// cell, and every other level renders as its own name.
    /// </summary>
    [Theory]
    [InlineData(CommentHealthLevel.NotApplicable, "")]
    [InlineData(CommentHealthLevel.Good, "Good")]
    [InlineData(CommentHealthLevel.Low, "Low")]
    public void HealthCell_RendersExpectedText(CommentHealthLevel health, string expected)
    {
        var cell = ColumnLayout.HealthCell(health);

        Assert.Equal(expected, cell);
    }
}
