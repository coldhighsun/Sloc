using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="IgnoreCaseComparer"/>.
/// </summary>
public class IgnoreCaseComparerTests
{
    /// <summary>
    /// Verifies that <see cref="IgnoreCaseComparer.Comparison"/> resolves to the ordinal
    /// case-insensitive or case-sensitive comparison matching the given flag.
    /// </summary>
    /// <param name="ignoreCase">The flag to resolve a comparison for.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Comparison_ResolvesOrdinalVariantMatchingFlag(bool ignoreCase)
    {
        var expected = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        Assert.Equal(expected, IgnoreCaseComparer.Comparison(ignoreCase));
    }

    /// <summary>
    /// Verifies that <see cref="IgnoreCaseComparer.Comparer"/> resolves to the ordinal
    /// case-insensitive or case-sensitive comparer matching the given flag.
    /// </summary>
    /// <param name="ignoreCase">The flag to resolve a comparer for.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Comparer_ResolvesOrdinalVariantMatchingFlag(bool ignoreCase)
    {
        var expected = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        Assert.Equal(expected, IgnoreCaseComparer.Comparer(ignoreCase));
    }
}
