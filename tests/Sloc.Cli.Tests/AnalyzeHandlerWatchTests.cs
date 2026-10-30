using Sloc.Cli.Analysis;
using Sloc.Cli.Output;
using Sloc.Core.Languages;
using Sloc.Core.Models;
using Sloc.Core.Scanning;
using Spectre.Console;

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
    /// Verifies that a percentage which rounds up to the threshold at one decimal is not
    /// reported as "5.0% is below the required 5.0%".
    /// </summary>
    [Fact]
    public void WatchExitCode_BelowThresholdByFraction_MessageShowsDistinctValues()
    {
        var summary = new AnalysisSummary([
            new FileAnalysis { Path = "a.cs", Language = "C#", Code = 2500, Comment = 130, Blank = 0 }
        ]);
        var original = Console.Error;
        using var writer = new StringWriter();
        Console.SetError(writer);
        int exitCode;
        try
        {
            exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 5 }, summary);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(ExitCode.ThresholdNotMet, exitCode);
        Assert.DoesNotContain("5.0% is below the required 5.0%", writer.ToString());
    }

    /// <summary>
    /// Verifies that an empty result set warns on stderr instead of silently passing the gate.
    /// </summary>
    [Fact]
    public void WatchExitCode_NoFilesAnalyzed_WarnsAndSucceeds()
    {
        var original = Console.Error;
        using var writer = new StringWriter();
        Console.SetError(writer);
        int exitCode;
        try
        {
            exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 50 }, new AnalysisSummary([]));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.Contains("no files were analyzed", writer.ToString());
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
    /// Verifies that when the watch loop's last rescan failed (e.g. the watched directory
    /// was removed), the exit code reports the error instead of judging the threshold
    /// against the stale pre-failure summary.
    /// </summary>
    [Fact]
    public void WatchExitCode_LastPassFailed_ReturnsError()
    {
        var summary = new AnalysisSummary([
            new FileAnalysis { Path = "a.cs", Language = "C#", Code = 1, Comment = 1, Blank = 0 }
        ]);

        var exitCode = AnalyzeHandler.WatchExitCode(new AnalyzeOptions { Path = ".", MinCommentPct = 50 }, summary, lastPassFailed: true);

        Assert.Equal(ExitCode.Error, exitCode);
    }

    /// <summary>
    /// Verifies that the summary shared by the first pass and every rescan applies the
    /// requested language sort and keeps the skipped entries.
    /// </summary>
    [Fact]
    public void BuildWatchSummary_AppliesSortAndKeepsSkipped()
    {
        var results = new List<FileAnalysis>
        {
            new() { Path = "a.cs", Language = "C#", Code = 1, Comment = 0, Blank = 0 },
            new() { Path = "b.py", Language = "Python", Code = 5, Comment = 0, Blank = 0 },
        };
        var skipped = new List<SkippedEntry> { new("c.bin", "binary file") };

        var summary = AnalyzeHandler.BuildWatchSummary(results, skipped, new AnalyzeOptions { Path = ".", Sort = LanguageSort.Name });

        Assert.Equal(["C#", "Python"], summary.ByLanguage.Select(l => l.Language));
        Assert.Single(summary.Skipped);
        Assert.Equal(2, summary.FileCount);
    }

    /// <summary>
    /// Verifies that an already-cancelled token (Ctrl+C during the first watch pass) aborts
    /// the analysis instead of running to completion.
    /// </summary>
    [Fact]
    public void AnalyzeFiles_CancelledToken_ThrowsOperationCanceled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sloc-cancel-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "int x;");
        try
        {
            Assert.True(LanguageRegistry.TryGetByExtension(".cs", out var language));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => new AnalyzeHandler().AnalyzeFiles(
                [new ScannedFile(path, language)],
                new AnalyzeOptions { Path = "." },
                [],
                cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies that a watch pass scans and analyzes the tree and returns the finished summary.
    /// </summary>
    [Fact]
    public void RunWatchPass_Uncancelled_ReturnsFinishedSummary()
    {
        var dir = Directory.CreateTempSubdirectory("sloc-first-pass-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.cs"), "int x;");

            var summary = new AnalyzeHandler().RunWatchPass(
                new AnalyzeOptions { Path = dir.FullName },
                new ScanOptions(),
                CancellationToken.None);

            Assert.Equal(1, summary.FileCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that Ctrl+C during the first scan aborts it instead of running to completion.
    /// </summary>
    [Fact]
    public void RunWatchPass_CancelledToken_ThrowsOperationCanceled()
    {
        var dir = Directory.CreateTempSubdirectory("sloc-first-pass-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.cs"), "int x;");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => new AnalyzeHandler().RunWatchPass(
                new AnalyzeOptions { Path = dir.FullName },
                new ScanOptions(),
                cts.Token));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that the first watch pass analyzes behind the live table, returns the finished
    /// summary, and draws it as the last frame.
    /// </summary>
    [Fact]
    public void AnalyzeFirstWatchPass_Uncancelled_ReturnsSummaryAndDrawsFinalFrame()
    {
        var dir = Directory.CreateTempSubdirectory("sloc-first-pass-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.cs");
            File.WriteAllText(path, "int x;");
            Assert.True(LanguageRegistry.TryGetByExtension(".cs", out var language));
            var frames = new List<Table>();

            var summary = new AnalyzeHandler().AnalyzeFirstWatchPass(
                frames.Add,
                new ScanResult([new ScannedFile(path, language)], []),
                new AnalyzeOptions { Path = dir.FullName },
                new TableRenderer(),
                CancellationToken.None);

            Assert.NotNull(summary);
            Assert.Equal(1, summary.FileCount);
            Assert.NotEmpty(frames);
            Assert.DoesNotContain("Cancelled", frames[^1].Caption?.Text ?? string.Empty);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that cancelling the first watch pass (Ctrl+C) returns no summary and replaces the
    /// live table with a cancelled frame instead of leaving partial totals on screen.
    /// </summary>
    [Fact]
    public void AnalyzeFirstWatchPass_Cancelled_ReturnsNullAndDrawsCancelledFrame()
    {
        var dir = Directory.CreateTempSubdirectory("sloc-first-pass-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.cs");
            File.WriteAllText(path, "int x;");
            Assert.True(LanguageRegistry.TryGetByExtension(".cs", out var language));
            var frames = new List<Table>();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var summary = new AnalyzeHandler().AnalyzeFirstWatchPass(
                frames.Add,
                new ScanResult([new ScannedFile(path, language)], []),
                new AnalyzeOptions { Path = dir.FullName },
                new TableRenderer(),
                cts.Token);

            Assert.Null(summary);
            Assert.Contains("Cancelled", frames[^1].Caption?.Text);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that the live-table analysis surfaces cancellation as an exception rather than
    /// returning partial results.
    /// </summary>
    [Fact]
    public void AnalyzeIntoLiveTable_CancelledToken_ThrowsOperationCanceled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sloc-cancel-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "int x;");
        try
        {
            Assert.True(LanguageRegistry.TryGetByExtension(".cs", out var language));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => new AnalyzeHandler().AnalyzeIntoLiveTable(
                _ => { },
                [new ScannedFile(path, language)],
                new AnalyzeOptions { Path = "." },
                [],
                new TableRenderer(),
                cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A path with no well-known build/VCS/package directory segment is not ignored.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrdinaryPaths))]
    public void IsInIgnoredDirectory_ReturnsFalse_ForOrdinaryPaths(string path)
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(WatchRoot, path));
    }

    public static IEnumerable<object[]> OrdinaryPaths()
    {
        yield return [Combine(WatchRoot, "src", "Program.cs")];
        yield return [Combine(WatchRoot, "README.md")];
    }

    /// <summary>
    /// A path with a well-known build/VCS/package directory segment anywhere along it is
    /// ignored, regardless of case or position.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExcludedDirectoryPaths))]
    public void IsInIgnoredDirectory_ReturnsTrue_ForKnownExcludedDirectories(string path)
    {
        Assert.True(AnalyzeHandler.IsInIgnoredDirectory(WatchRoot, path));
    }

    public static IEnumerable<object[]> ExcludedDirectoryPaths()
    {
        yield return [Combine(WatchRoot, "bin", "Debug", "net8.0", "sloc.dll")];
        yield return [Combine(WatchRoot, "obj", "Debug", "Sloc.csproj.nuget.g.props")];
        yield return [Combine(WatchRoot, ".git", "index")];
        yield return [Combine(WatchRoot, "node_modules", "typescript", "lib", "tsc.js")];
        yield return [Combine(WatchRoot, "src", "NODE_MODULES", "pkg", "index.js")];
        yield return [Combine(WatchRoot, "artifacts", "bin", "Sloc.Cli", "debug", "sloc.dll")];
    }

    /// <summary>
    /// A file whose own name merely contains an excluded directory name as a substring
    /// (rather than as a full path segment) is not ignored.
    /// </summary>
    [Fact]
    public void IsInIgnoredDirectory_DoesNotMatchSubstringOfASegment()
    {
        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(WatchRoot, Combine(WatchRoot, "src", "binary_search.cs")));
    }

    /// <summary>
    /// An excluded directory name above the watched root (e.g. watching <c>~/bin/project</c>)
    /// does not make every event under the root look ignored; only segments below the root
    /// count, matching how the scanner applies its default excludes relative to the scan root.
    /// </summary>
    [Fact]
    public void IsInIgnoredDirectory_IgnoresSegmentsAboveTheWatchRoot()
    {
        var root = Combine(WatchRoot, "bin", "project");

        Assert.False(AnalyzeHandler.IsInIgnoredDirectory(root, Combine(root, "src", "Program.cs")));
        Assert.True(AnalyzeHandler.IsInIgnoredDirectory(root, Combine(root, "obj", "project.assets.json")));
    }

    private static readonly string WatchRoot = Path.Combine(Path.GetTempPath(), "repo");

    // IsInIgnoredDirectory splits on the platform's own directory separators, so test paths
    // must be built with Path.Combine rather than hardcoded Windows-style backslashes to
    // behave the same way on Linux CI as on a Windows dev machine.
    private static string Combine(params string[] segments) => Path.Combine(segments);
}
