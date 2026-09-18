using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="RelativePathResolver"/>.
/// </summary>
public class RelativePathResolverTests
{
    /// <summary>
    /// Verifies that an empty base directory (the scan root) passes the path through
    /// unchanged.
    /// </summary>
    [Fact]
    public void TryGetRelativePath_EmptyBaseDirectory_ReturnsPathUnchanged()
    {
        var result = RelativePathResolver.TryGetRelativePath(string.Empty, "src/Foo.cs", out var relative);

        Assert.True(result);
        Assert.Equal("src/Foo.cs", relative);
    }

    /// <summary>
    /// Verifies that a path nested under the base directory is rebased relative to it.
    /// </summary>
    [Fact]
    public void TryGetRelativePath_PathNestedUnderBase_ReturnsRebasedPath()
    {
        var result = RelativePathResolver.TryGetRelativePath("src", "src/sub/Foo.cs", out var relative);

        Assert.True(result);
        Assert.Equal("sub/Foo.cs", relative);
    }

    /// <summary>
    /// Verifies that a path equal to the base directory itself is rejected, since it
    /// requires a '/' boundary after the base directory.
    /// </summary>
    [Fact]
    public void TryGetRelativePath_PathEqualsBase_ReturnsFalse()
    {
        var result = RelativePathResolver.TryGetRelativePath("src", "src", out var relative);

        Assert.False(result);
        Assert.Equal(string.Empty, relative);
    }

    /// <summary>
    /// Verifies that a path outside the base directory is rejected.
    /// </summary>
    [Fact]
    public void TryGetRelativePath_PathNotUnderBase_ReturnsFalse()
    {
        var result = RelativePathResolver.TryGetRelativePath("src", "other/Foo.cs", out var relative);

        Assert.False(result);
        Assert.Equal(string.Empty, relative);
    }

    /// <summary>
    /// Verifies that a path that merely shares the base directory's name as a prefix,
    /// without a '/' boundary, is not treated as nested under it.
    /// </summary>
    [Fact]
    public void TryGetRelativePath_PrefixWithoutSeparator_ReturnsFalse()
    {
        var result = RelativePathResolver.TryGetRelativePath("src", "src-extra/Foo.cs", out var relative);

        Assert.False(result);
        Assert.Equal(string.Empty, relative);
    }
}
