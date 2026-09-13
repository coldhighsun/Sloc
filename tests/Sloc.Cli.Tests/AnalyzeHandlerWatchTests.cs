using Sloc.Cli.Analysis;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="AnalyzeHandler.IsInIgnoredDirectory"/>, the cheap
/// noise filter <c>--watch</c> applies to <see cref="System.IO.FileSystemWatcher"/> events
/// before triggering a rescan.
/// </summary>
public class AnalyzeHandlerWatchTests
{
    /// <summary>
    /// A path with no well-known build/VCS/package directory segment is not ignored.
    /// </summary>
    [Theory]
    [InlineData(@"C:\repo\src\Program.cs")]
    [InlineData(@"C:\repo\README.md")]
    [InlineData("/home/me/repo/src/lib.rs")]
    public void IsInIgnoredDirectory_ReturnsFalse_ForOrdinaryPaths(string path)
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(path));
    }

    /// <summary>
    /// A path with a well-known build/VCS/package directory segment anywhere along it is
    /// ignored, regardless of case or position.
    /// </summary>
    [Theory]
    [InlineData(@"C:\repo\bin\Debug\net8.0\sloc.dll")]
    [InlineData(@"C:\repo\obj\Debug\Sloc.csproj.nuget.g.props")]
    [InlineData(@"C:\repo\.git\index")]
    [InlineData(@"C:\repo\node_modules\typescript\lib\tsc.js")]
    [InlineData(@"C:\repo\src\NODE_MODULES\pkg\index.js")]
    [InlineData("/home/me/repo/artifacts/bin/Sloc.Cli/debug/sloc.dll")]
    public void IsInIgnoredDirectory_ReturnsTrue_ForKnownExcludedDirectories(string path)
    {
        Assert.True(AnalyzeHandler.IsInIgnoredDirectory(path));
    }

    /// <summary>
    /// A file whose own name merely contains an excluded directory name as a substring
    /// (rather than as a full path segment) is not ignored.
    /// </summary>
    [Fact]
    public void IsInIgnoredDirectory_DoesNotMatchSubstringOfASegment()
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(@"C:\repo\src\binary_search.cs"));
    }
}
