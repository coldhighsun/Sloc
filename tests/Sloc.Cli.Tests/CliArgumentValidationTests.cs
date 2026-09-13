using Sloc.Cli.Analysis;
using Sloc.Cli.Parsing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="CliArgumentValidation"/>.
/// </summary>
public class CliArgumentValidationTests
{
    /// <summary>
    /// Verifies the boundary behavior of <c>--min-comment-pct</c>: <see langword="null"/>
    /// (not supplied) and the inclusive 0–100 range are valid; anything outside it is not.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0.0, true)]
    [InlineData(100.0, true)]
    [InlineData(50.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(100.1, false)]
    public void IsValidMinCommentPct_ChecksInclusiveRange(double? value, bool expected)
    {
        Assert.Equal(expected, CliArgumentValidation.IsValidMinCommentPct(value));
    }

    /// <summary>
    /// Verifies the boundary behavior of <c>--top</c>: <see langword="null"/> (not supplied)
    /// and any value 1 or greater are valid; 0 or negative is not.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidTop_RequiresAtLeastOne(int? value, bool expected)
    {
        Assert.Equal(expected, CliArgumentValidation.IsValidTop(value));
    }

    /// <summary>
    /// When <c>--watch</c> is not supplied, no other option combination should be rejected.
    /// </summary>
    [Fact]
    public void ValidateWatch_ReturnsNull_WhenWatchNotRequested()
    {
        Assert.Null(CliArgumentValidation.ValidateWatch(
            watch: false, listFile: "files.txt", gitHash: "HEAD", format: OutputFormat.Json, baselinePath: "baseline.json"));
    }

    /// <summary>
    /// A plain <c>--watch</c> with Table format and no conflicting options is valid.
    /// </summary>
    [Fact]
    public void ValidateWatch_ReturnsNull_ForValidCombination()
    {
        Assert.Null(CliArgumentValidation.ValidateWatch(
            watch: true, listFile: null, gitHash: null, format: OutputFormat.Table, baselinePath: null));
    }

    /// <summary>
    /// <c>--watch</c> rejects <c>--list-file</c> before checking format or baseline.
    /// </summary>
    [Fact]
    public void ValidateWatch_RejectsListFile()
    {
        var error = CliArgumentValidation.ValidateWatch(
            watch: true, listFile: "files.txt", gitHash: null, format: OutputFormat.Table, baselinePath: null);

        Assert.Equal("sloc: --watch cannot be used with --git-hash or --list-file.", error);
    }

    /// <summary>
    /// <c>--watch</c> rejects <c>--git-hash</c>.
    /// </summary>
    [Fact]
    public void ValidateWatch_RejectsGitHash()
    {
        var error = CliArgumentValidation.ValidateWatch(
            watch: true, listFile: null, gitHash: "HEAD", format: OutputFormat.Table, baselinePath: null);

        Assert.Equal("sloc: --watch cannot be used with --git-hash or --list-file.", error);
    }

    /// <summary>
    /// <c>--watch</c> rejects any non-Table format.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Json)]
    [InlineData(OutputFormat.Html)]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Markdown)]
    public void ValidateWatch_RejectsNonTableFormat(OutputFormat format)
    {
        var error = CliArgumentValidation.ValidateWatch(
            watch: true, listFile: null, gitHash: null, format: format, baselinePath: null);

        Assert.Equal("sloc: --watch only supports Table format.", error);
    }

    /// <summary>
    /// <c>--watch</c> rejects <c>--baseline</c>.
    /// </summary>
    [Fact]
    public void ValidateWatch_RejectsBaseline()
    {
        var error = CliArgumentValidation.ValidateWatch(
            watch: true, listFile: null, gitHash: null, format: OutputFormat.Table, baselinePath: "baseline.json");

        Assert.Equal("sloc: --watch cannot be used with --baseline.", error);
    }
}