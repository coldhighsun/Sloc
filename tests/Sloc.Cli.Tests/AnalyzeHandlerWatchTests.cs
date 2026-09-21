using Sloc.Cli.Analysis;
using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="AnalyzeHandler.IsInIgnoredDirectory"/>, the cheap
/// noise filter <c>--watch</c> applies to <see cref="System.IO.FileSystemWatcher"/> events
/// before triggering a rescan, and <see cref="AnalyzeHandler.WatchExitCode"/>, which decides
/// the process exit code once the watch loop ends.
/// </summary>
public class AnalyzeHandlerWatchTests
{
    /// <summary>
    /// Verifies that <c>--watch --min-comment-pct</c> reports the threshold exit code when
    /// the last watched summary is below the requested minimum, matching every other code
    /// path (<c>Execute</c>, baseline/compare-to) which enforces the same gate.
    /// </summary>
    [Fact]
    public void WatchExitCode_BelowCommentThreshold_ReturnsThresholdCode()
    {
        var summary = new AnalysisSummary([
            new FileAnalysis { Path = "a.cs", Language = "C#", Code = 2, Comment = 0, Blank = 0 }
        ]);

        var exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 50 }, summary);

        Assert.Equal(ExitCode.ThresholdNotMet, exitCode);
    }

    /// <summary>
    /// Verifies that <c>--watch --min-comment-pct</c> still succeeds when the last watched
    /// summary meets the requested minimum.
    /// </summary>
    [Fact]
    public void WatchExitCode_MeetsCommentThreshold_ReturnsSuccess()
    {
        var summary = new AnalysisSummary([
            new FileAnalysis { Path = "a.cs", Language = "C#", Code = 1, Comment = 1, Blank = 0 }
        ]);

        var exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 50 }, summary);

        Assert.Equal(ExitCode.Success, exitCode);
    }

    /// <summary>
    /// Verifies that no threshold was ever configured, or the watch loop ended before
    /// completing a single pass, still yields success rather than throwing.
    /// </summary>
    [Fact]
    public void WatchExitCode_NoSummary_ReturnsSuccess()
    {
        var exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 50 }, lastSummary: null);

        Assert.Equal(ExitCode.Success, exitCode);
    }

    /// <summary>
    /// A path with no well-known build/VCS/package directory segment is not ignored.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrdinaryPaths))]
    public void IsInIgnoredDirectory_ReturnsFalse_ForOrdinaryPaths(string path)
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(path));
    }

    public static IEnumerable<object[]> OrdinaryPaths()
    {
        yield return [Combine("repo", "src", "Program.cs")];
        yield return [Combine("repo", "README.md")];
    }

    /// <summary>
    /// A path with a well-known build/VCS/package directory segment anywhere along it is
    /// ignored, regardless of case or position.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExcludedDirectoryPaths))]
    public void IsInIgnoredDirectory_ReturnsTrue_ForKnownExcludedDirectories(string path)
    {
        Assert.True(AnalyzeHandler.IsInIgnoredDirectory(path));
    }

    public static IEnumerable<object[]> ExcludedDirectoryPaths()
    {
        yield return [Combine("repo", "bin", "Debug", "net8.0", "sloc.dll")];
        yield return [Combine("repo", "obj", "Debug", "Sloc.csproj.nuget.g.props")];
        yield return [Combine("repo", ".git", "index")];
        yield return [Combine("repo", "node_modules", "typescript", "lib", "tsc.js")];
        yield return [Combine("repo", "src", "NODE_MODULES", "pkg", "index.js")];
        yield return [Combine("repo", "artifacts", "bin", "Sloc.Cli", "debug", "sloc.dll")];
    }

    /// <summary>
    /// A file whose own name merely contains an excluded directory name as a substring
    /// (rather than as a full path segment) is not ignored.
    /// </summary>
    [Fact]
    public void IsInIgnoredDirectory_DoesNotMatchSubstringOfASegment()
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(Combine("repo", "src", "binary_search.cs")));
    }

    // IsInIgnoredDirectory splits on the platform's own directory separators, so test paths
    // must be built with Path.Combine rather than hardcoded Windows-style backslashes to
    // behave the same way on Linux CI as on a Windows dev machine.
    private static string Combine(params string[] segments) => Path.Combine(segments);
}
