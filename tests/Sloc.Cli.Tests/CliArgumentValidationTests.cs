using Sloc.Cli;

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
}
