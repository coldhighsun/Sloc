using Sloc.Core.Models;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="CommentHealth"/>.
/// </summary>
public class CommentHealthTests
{
    /// <summary>
    /// Verifies that a language which does not support health analysis always maps to
    /// <see cref="CommentHealthLevel.NotApplicable"/>, regardless of counts.
    /// </summary>
    [Fact]
    public void Classify_UnsupportedLanguage_ReturnsNotApplicable()
    {
        var level = CommentHealth.Classify("JSON", code: 100, comment: 50);

        Assert.Equal(CommentHealthLevel.NotApplicable, level);
    }

    /// <summary>
    /// Verifies that an unknown language name is treated as unsupported.
    /// </summary>
    [Fact]
    public void Classify_UnknownLanguage_ReturnsNotApplicable()
    {
        var level = CommentHealth.Classify("Nonexistent", code: 10, comment: 10);

        Assert.Equal(CommentHealthLevel.NotApplicable, level);
    }

    /// <summary>
    /// Verifies that a supported language with no code or comment lines maps to
    /// <see cref="CommentHealthLevel.NotApplicable"/>.
    /// </summary>
    [Fact]
    public void Classify_SupportedButNoLines_ReturnsNotApplicable()
    {
        var level = CommentHealth.Classify("C#", code: 0, comment: 0);

        Assert.Equal(CommentHealthLevel.NotApplicable, level);
    }

    /// <summary>
    /// Verifies that the density thresholds map to the expected buckets for a supported language.
    /// </summary>
    /// <param name="code">The number of code lines.</param>
    /// <param name="comment">The number of comment lines.</param>
    /// <param name="expected">The expected health level.</param>
    [Theory]
    [InlineData(100, 0, CommentHealthLevel.None)]
    [InlineData(100, 2, CommentHealthLevel.Low)]
    [InlineData(100, 7, CommentHealthLevel.Fair)]
    [InlineData(100, 20, CommentHealthLevel.Good)]
    [InlineData(100, 50, CommentHealthLevel.High)]
    [InlineData(10, 90, CommentHealthLevel.Dense)]
    public void Classify_SupportedLanguage_MapsDensityToBucket(int code, int comment, CommentHealthLevel expected)
    {
        var level = CommentHealth.Classify("C#", code, comment);

        Assert.Equal(expected, level);
    }

    /// <summary>
    /// Verifies that the explicit-support overload honors the supported flag and counts.
    /// </summary>
    [Fact]
    public void Classify_ExplicitSupportOverload_HonorsFlag()
    {
        Assert.Equal(CommentHealthLevel.NotApplicable, CommentHealth.Classify(supported: false, code: 100, comment: 50));
        Assert.Equal(CommentHealthLevel.Good, CommentHealth.Classify(supported: true, code: 100, comment: 20));
    }
}
