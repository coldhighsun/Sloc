using Sloc.Cli.Output;
using Sloc.Cli.Updates;
using Sloc.Core.Models;
using Sloc.Core.Scanning;
using Spectre.Console;
using System.Reflection;

namespace Sloc.Cli.Analysis;

/// <summary>
/// The stateless scan/analyze/render plumbing <see cref="AnalyzeHandler"/>'s orchestration
/// methods (<see cref="AnalyzeHandler.Execute"/>, <see cref="AnalyzeHandler.ExecuteWatch"/>,
/// and friends) delegate to: run-context setup, output-format dispatch, path remapping and
/// deduplication, and the update/threshold reporting shared by every exit path.
/// </summary>
public sealed partial class AnalyzeHandler
{
    /// <summary>
    /// Prefix of the <see cref="SkippedEntry.Reason"/> string <see cref="DeduplicateByHash"/>
    /// produces, so <see cref="RemapGitPaths(List{SkippedEntry}, Dictionary{string, string})"/>
    /// can find and remap the temp path it embeds after a <c>--git-hash</c>/<c>--compare-to</c> run.
    /// </summary>
    private const string DuplicateReasonPrefix = "duplicate of ";

    private const string StdoutToken = "-";

    // Mirrors DirectoryScanner's default-excluded directory names (kept separately since
    // that set is private to DirectoryScanner); see IsInIgnoredDirectory.
    private static readonly string[] WatchIgnoredDirectoryNames =
        ["bin", "obj", "artifacts", ".git", ".vs", ".vscode", ".idea", "node_modules"];

    /// <summary>
    /// The per-run state <see cref="Execute"/> assembles once up front and threads through
    /// its scan/analyze/render stages: the resolved display version and source path, whether
    /// progress UI may be drawn, the <see cref="ScanOptions"/> derived from <see cref="AnalyzeOptions"/>,
    /// the shared <see cref="TableRenderer"/> instance, and the in-flight update-check task.
    /// </summary>
    /// <param name="Version">The tool's own informational version, or <see langword="null"/> if unavailable.</param>
    /// <param name="SourcePath">The resolved path/list-file/git-hash description surfaced in report metadata.</param>
    /// <param name="ShowProgress">Whether Spectre progress UI (spinner/live table/progress bar) may be drawn.</param>
    /// <param name="ScanOptions">The scan options derived from the parsed CLI options.</param>
    /// <param name="TableRenderer">The table renderer shared across the scan/analyze/render stages.</param>
    /// <param name="UpdateCheck">The in-flight update-check task, or <see langword="null"/> if it was not started.</param>
    private sealed record RunContext(
        string? Version,
        string SourcePath,
        bool ShowProgress,
        ScanOptions ScanOptions,
        TableRenderer TableRenderer,
        Task<UpdateCheckResult?>? UpdateCheck);

    /// <summary>
    /// Whether <paramref name="path"/> falls under one of the well-known build/VCS/package
    /// directories that <see cref="DirectoryScanner"/> always excludes by default, used to
    /// cheaply filter obviously-irrelevant <see cref="FileSystemWatcher"/> events in
    /// <see cref="ExecuteWatch"/> without replaying the scanner's full glob/gitignore logic.
    /// Only segments below <paramref name="watchRoot"/> are considered, matching how the
    /// scanner applies those excludes relative to the scan root (so watching e.g.
    /// <c>~/bin/project</c> still reacts to changes).
    /// </summary>
    /// <param name="watchRoot">The directory being watched.</param>
    /// <param name="path">The filesystem path reported by a <see cref="FileSystemWatcher"/> event.</param>
    internal static bool IsInIgnoredDirectory(string watchRoot, string path) =>
        Path.GetRelativePath(watchRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => WatchIgnoredDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Determines <see cref="ExecuteWatch"/>'s exit code once the watch loop ends: the same
    /// <c>--min-comment-pct</c> threshold gate every other code path applies, evaluated
    /// against the last summary the watch loop rendered (or a plain success if the loop
    /// exited before ever completing a pass). If the loop's last rescan failed (e.g. the
    /// watched directory was removed), the last summary is stale, so it reports
    /// <see cref="ExitCode.Error"/> instead of judging the threshold against it.
    /// </summary>
    /// <param name="options">The parsed options, used for <see cref="AnalyzeOptions.MinCommentPct"/>.</param>
    /// <param name="lastSummary">The last summary the watch loop rendered, or <see langword="null"/> if none.</param>
    /// <param name="lastPassFailed">Whether the watch loop's most recent rescan failed.</param>
    internal static int WatchExitCode(AnalyzeOptions options, AnalysisSummary? lastSummary, bool lastPassFailed = false) =>
        lastPassFailed ? ExitCode.Error
        : lastSummary is null ? ExitCode.Success
        : ThresholdResult(options, lastSummary);

    /// <summary>
    /// Resolves the tool version and display source path, prints the startup banner (Table
    /// format only, unless quiet/redirected/watch), starts the background update check, and
    /// derives <see cref="ScanOptions"/> from <paramref name="options"/>. Called once at the
    /// start of <see cref="Execute"/>.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    private static RunContext BuildRunContext(AnalyzeOptions options)
    {
        var version = typeof(AnalyzeHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        // The path/list-file/git-hash the current run analyzed, surfaced in report
        // metadata (Table banner, Json/Html/Markdown) so a saved or shared report can be
        // traced back to its source. The scanned directory/file is always resolved to a
        // full absolute path so the report is unambiguous regardless of the working
        // directory it was generated from (e.g. "." becomes "C:\repo").
        var resolvedPath = ResolveFullPath(options.Path);
        var sourcePath = options.GitHash is { } gitHashForDisplay
            ? $"{resolvedPath} @ {gitHashForDisplay}"
            : options.ListFile is { } listFileForDisplay
                ? $"list: {ResolveFullPath(listFileForDisplay)}"
                : resolvedPath;

        if (options.Format == OutputFormat.Table && !Console.IsOutputRedirected && !options.Quiet && !options.Watch)
        {
            if (!string.IsNullOrEmpty(version))
            {
                AnsiConsole.MarkupLine($"[grey]sloc {Markup.Escape(version)}[/]");
            }

            AnsiConsole.MarkupLine($"[grey]Analyzing: {Markup.Escape(sourcePath)}[/]");
        }

        // Start the update check concurrently with the scan/analysis so a slow network
        // never delays the actual work. It is awaited and reported at the end of the run.
        Task<UpdateCheckResult?>? updateCheck = null;
        if (!options.NoUpdateCheck && !options.Quiet && !string.IsNullOrEmpty(version))
        {
            updateCheck = new UpdateChecker()
                .CheckForUpdateAsync(version, TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        // Spectre's live table / progress bar drive the console cursor, which throws when
        // stdout is redirected (pipes, CI, files). Suppress it there and when the caller
        // asked for a quiet / no-progress run.
        var showProgress = !Console.IsOutputRedirected && !options.Quiet && !options.NoProgress;

        var scanOptions = new ScanOptions
        {
            Includes = options.Includes,
            Excludes = options.Excludes,
            IncludeLangs = options.IncludeLangs,
            ExcludeLangs = options.ExcludeLangs,
            Recursive = !options.NoRecursive,
            IncludeUnknown = options.IncludeUnknown,
            RespectGitignore = options.RespectGitignore,
            RespectGitAttributes = options.RespectGitAttributes,
            FollowSymlinks = options.FollowSymlinks
        };

        return new RunContext(version, sourcePath, showProgress, scanOptions, new TableRenderer(), updateCheck);
    }

    /// <summary>
    /// Returns a factory for the <see cref="IResultRenderer"/> matching <paramref name="format"/>,
    /// or <see langword="null"/> for <see cref="OutputFormat.Table"/> (rendered separately, since
    /// it writes directly to the console rather than through a <see cref="TextWriter"/>). A
    /// factory (rather than a single instance) is returned so the same format can be rendered
    /// twice with different writers, e.g. once to stdout and once to a file.
    /// </summary>
    /// <param name="format">The requested output format.</param>
    private static Func<TextWriter, IResultRenderer>? CreateRenderer(OutputFormat format) => format switch
    {
        OutputFormat.Json => writer => new JsonRenderer(writer),
        OutputFormat.Html => writer => new HtmlRenderer(writer),
        OutputFormat.Csv => writer => new CsvRenderer(writer),
        OutputFormat.Markdown => writer => new MarkdownRenderer(writer),
        _ => null
    };

    /// <summary>
    /// Keeps only the first file (in scan order) for each distinct content hash; every
    /// later duplicate is removed from <paramref name="results"/> and added to
    /// <paramref name="skipped"/> so its lines aren't double-counted.
    /// </summary>
    /// <param name="results">The analyzed files, in scan order.</param>
    /// <param name="skipped">The skipped-entries list a removed duplicate is appended to.</param>
    private static List<FileAnalysis> DeduplicateByHash(List<FileAnalysis> results, List<SkippedEntry> skipped)
    {
        var unique = new List<FileAnalysis>(results.Count);
        var firstPathByHash = new Dictionary<string, string>();

        foreach (var analysis in results)
        {
            if (analysis.Hash is not { } hash || !firstPathByHash.TryGetValue(hash, out var firstPath))
            {
                if (analysis.Hash is { } h)
                {
                    firstPathByHash[h] = analysis.Path;
                }

                unique.Add(analysis);
                continue;
            }

            skipped.Add(new SkippedEntry(analysis.Path, DuplicateReasonPrefix + firstPath));
        }

        return unique;
    }

    /// <summary>
    /// Lazily yields every line of <paramref name="reader"/>.
    /// </summary>
    /// <param name="reader">The reader to read lines from.</param>
    private static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Reads a <c>--list-file</c>'s entries, one per non-blank line, from either the file at
    /// <paramref name="listFile"/> or, when it is the stdin sentinel (<c>-</c>), from
    /// <see cref="Console.In"/>.
    /// </summary>
    /// <param name="listFile">The list-file path, or <c>-</c> to read from stdin.</param>
    private static IEnumerable<string> ReadListFile(string listFile)
    {
        var lines = listFile == StdoutToken
            ? ReadAllLines(Console.In)
            : File.ReadLines(listFile);

        return lines.Where(line => !string.IsNullOrWhiteSpace(line));
    }

    /// <summary>
    /// Replaces each analysis's temporary extraction path with its original git-relative
    /// path, so downstream rendering and <c>--baseline</c> diffing never see a temp path.
    /// </summary>
    /// <param name="analyses">The analyses to remap, in place.</param>
    /// <param name="gitPathByTempPath">The temp-path-to-git-path mapping from the extracted snapshot.</param>
    private static List<FileAnalysis> RemapGitPaths(List<FileAnalysis> analyses, Dictionary<string, string> gitPathByTempPath)
    {
        for (var i = 0; i < analyses.Count; i++)
        {
            if (gitPathByTempPath.TryGetValue(analyses[i].Path, out var gitPath))
            {
                var analysis = analyses[i];
                analyses[i] = new FileAnalysis
                {
                    Path = gitPath,
                    Language = analysis.Language,
                    Code = analysis.Code,
                    Comment = analysis.Comment,
                    Blank = analysis.Blank,
                    Complexity = analysis.Complexity,
                    Hash = analysis.Hash
                };
            }
        }

        return analyses;
    }

    /// <summary>
    /// Replaces each skipped entry's temporary extraction path with its original
    /// git-relative path, including the path embedded in a <see cref="DeduplicateByHash"/>
    /// "duplicate of" reason, so no temporary path survives into the rendered output.
    /// </summary>
    /// <param name="skipped">The skipped entries to remap, in place.</param>
    /// <param name="gitPathByTempPath">The temp-path-to-git-path mapping from the extracted snapshot.</param>
    private static List<SkippedEntry> RemapGitPaths(List<SkippedEntry> skipped, Dictionary<string, string> gitPathByTempPath)
    {
        for (var i = 0; i < skipped.Count; i++)
        {
            var entry = skipped[i];

            if (gitPathByTempPath.TryGetValue(entry.Path, out var gitPath))
            {
                entry = entry with
                {
                    Path = gitPath
                };
            }

            if (entry.Reason.StartsWith(DuplicateReasonPrefix, StringComparison.Ordinal)
                && gitPathByTempPath.TryGetValue(entry.Reason[DuplicateReasonPrefix.Length..], out var duplicateGitPath))
            {
                entry = entry with
                {
                    Reason = DuplicateReasonPrefix + duplicateGitPath
                };
            }

            skipped[i] = entry;
        }

        return skipped;
    }

    /// <summary>
    /// Renders a non-diff result: a structured format (Json/Html/Csv/Markdown) to stdout or
    /// <c>--output</c>, or otherwise the Table-format views (no-matches message, by-file table,
    /// and/or the language table when it was not already drawn by the live-table progress UI),
    /// followed by the skipped-files section.
    /// </summary>
    /// <param name="summary">The summary to render.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="sourcePath">The resolved source path/list-file/git-hash description to surface in the output.</param>
    /// <param name="tableRenderer">The table renderer to use for Table-format views.</param>
    /// <param name="showProgress">Whether the live-table progress UI already drew the language table.</param>
    private static int RenderNormal(AnalysisSummary summary, AnalyzeOptions options, string sourcePath, TableRenderer tableRenderer, bool showProgress)
    {
        if (CreateRenderer(options.Format) is { } createRenderer)
        {
            // These formats default to stdout (pipeable/pasteable); an explicit path writes a file.
            if (options.OutputFile is null || options.OutputFile == StdoutToken)
            {
                createRenderer(Console.Out).Render(summary, options.ByFile, options.NoHealth, options.Detailed, sourcePath, options.NoComplexity);
            }
            else if (!WriteToFile(options.OutputFile, writer => createRenderer(writer).Render(summary, options.ByFile, options.NoHealth, options.Detailed, sourcePath, options.NoComplexity), options.Quiet))
            {
                return ExitCode.Error;
            }

            return ExitCode.Success;
        }

        if (summary.FileCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files matched.[/]");
        }
        else if (options.ByFile)
        {
            tableRenderer.RenderByFile(summary, options.NoHealth, options.Paged, options.NoComplexity);
        }
        else if (!showProgress)
        {
            // The live table only renders during progress; render it here otherwise.
            AnsiConsole.Write(tableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth, noComplexity: options.NoComplexity));
        }

        tableRenderer.RenderSkipped(summary);
        return ExitCode.Success;
    }

    /// <summary>
    /// Awaits <paramref name="updateCheck"/>, if any, and prints a newer-version notice to
    /// stderr when one is available. Never throws: an update check must not break a normal
    /// analysis run.
    /// </summary>
    /// <param name="updateCheck">The in-flight update-check task, or <see langword="null"/> if it was not started.</param>
    /// <param name="currentVersion">The tool's own informational version, used to skip reporting when unavailable.</param>
    private static void ReportUpdate(Task<UpdateCheckResult?>? updateCheck, string? currentVersion)
    {
        if (updateCheck is null || string.IsNullOrEmpty(currentVersion))
        {
            return;
        }

        try
        {
            var result = updateCheck.GetAwaiter().GetResult();
            if (result is not null)
            {
                Console.Error.WriteLine(
                    $"A new version of sloc is available: {result.LatestVersion} (current: {currentVersion})");
                Console.Error.WriteLine($"Download: {result.ReleaseUrl}");
            }
        }
        catch
        {
            // An update check must never break a normal analysis run.
        }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to a full absolute path for display purposes (e.g.
    /// the "Analyzing:" banner and report metadata), so a relative input like "." is shown
    /// unambiguously. The stdin sentinel ("-") is returned unchanged since it isn't a
    /// filesystem path. Falls back to the original value if it cannot be resolved (e.g.
    /// invalid path characters), since this is display-only and must never fail the run.
    /// </summary>
    /// <param name="path">The path to resolve, or the stdin sentinel (<c>-</c>).</param>
    private static string ResolveFullPath(string path)
    {
        if (path == StdoutToken)
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Evaluates the <c>--min-comment-pct</c> threshold gate against <paramref name="summary"/>,
    /// printing an error to stderr and returning <see cref="ExitCode.ThresholdNotMet"/> when it
    /// is not met.
    /// </summary>
    /// <param name="options">The parsed options, used for <see cref="AnalyzeOptions.MinCommentPct"/>.</param>
    /// <param name="summary">The summary to check the comment percentage of.</param>
    private static int ThresholdResult(AnalyzeOptions options, AnalysisSummary summary)
    {
        if (options.MinCommentPct is { } min && summary.FileCount > 0 && summary.CommentPct < min)
        {
            Console.Error.WriteLine(
                $"sloc: comment percentage {summary.CommentPct:F1}% is below the required {min:F1}%.");
            return ExitCode.ThresholdNotMet;
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// Writes rendered output to <paramref name="path"/> as UTF-8 without a BOM, reporting a
    /// success message unless <paramref name="quiet"/>. Returns <see langword="false"/>
    /// (and prints an error to stderr) if the file could not be written.
    /// </summary>
    /// <param name="path">The output file path.</param>
    /// <param name="render">Writes the rendered output to the given <see cref="TextWriter"/>.</param>
    /// <param name="quiet">Whether to suppress the success message.</param>
    private static bool WriteToFile(string path, Action<TextWriter> render, bool quiet)
    {
        try
        {
            // UTF-8 without a BOM so piped/consumed files (e.g. via jq) parse cleanly.
            using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                render(writer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"sloc: could not write to '{path}': {ex.Message}");
            return false;
        }

        if (!quiet)
        {
            AnsiConsole.MarkupLine($"[green]Saved to:[/] {Markup.Escape(path)}");
        }

        return true;
    }
}