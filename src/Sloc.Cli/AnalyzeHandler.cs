using Sloc.Cli.Output;
using Sloc.Core;
using Sloc.Core.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Reflection;

namespace Sloc.Cli;

/// <summary>
/// The supported output formats.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// A human-readable, colored table.
    /// </summary>
    Table,

    /// <summary>
    /// Machine-readable JSON.
    /// </summary>
    Json,

    /// <summary>
    /// A human-readable HTML report.
    /// </summary>
    Html
}

/// <summary>
/// Process exit codes returned by the CLI.
/// </summary>
public static class ExitCode
{
    /// <summary>
    /// The requested path was not found or could not be read.
    /// </summary>
    public const int Error = 1;

    /// <summary>
    /// The run completed successfully.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// A configured threshold (e.g. <c>--min-comment-pct</c>) was not met.
    /// </summary>
    public const int ThresholdNotMet = 2;

    /// <summary>
    /// An unexpected error occurred.
    /// </summary>
    public const int Unexpected = 3;
}

/// <summary>
/// The parsed options for an analysis run.
/// </summary>
public sealed class AnalyzeOptions
{
    /// <summary>
    /// When set, a previously saved JSON report to compare the current run against; the
    /// output becomes a diff of line counts rather than the normal report.
    /// </summary>
    public string? BaselinePath
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, a per-file breakdown is shown.
    /// </summary>
    public bool ByFile
    {
        get; init;
    }

    /// <summary>
    /// Glob patterns of files to exclude.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// The output format.
    /// </summary>
    public OutputFormat Format
    {
        get; init;
    }

    /// <summary>
    /// Glob patterns of files to include.
    /// </summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, files with unknown extensions are included.
    /// </summary>
    public bool IncludeUnknown
    {
        get; init;
    }

    /// <summary>
    /// The maximum number of files to analyze in parallel. When <see langword="null"/>
    /// or non-positive, <see cref="Environment.ProcessorCount"/> is used. Set to 1 for
    /// fully sequential analysis.
    /// </summary>
    public int? Jobs
    {
        get; init;
    }

    /// <summary>
    /// When set, the run fails (returns <see cref="ExitCode.ThresholdNotMet"/>) if the
    /// overall comment percentage (comment lines / total lines) is below this value.
    /// </summary>
    public double? MinCommentPct
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, the Comment Health column and percentage
    /// breakdowns are hidden.
    /// </summary>
    public bool NoHealth
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, suppresses the live table and progress bar while
    /// still printing the banner and result.
    /// </summary>
    public bool NoProgress
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, subdirectories are not scanned.
    /// </summary>
    public bool NoRecursive
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, skips the GitHub check for a newer release.
    /// </summary>
    public bool NoUpdateCheck
    {
        get; init;
    }

    /// <summary>
    /// The output file path for Json and Html formats.
    /// When <see langword="null"/>, a default name is used.
    /// </summary>
    public string? OutputFile
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, the per-file console output is paginated.
    /// </summary>
    public bool Paged
    {
        get; init;
    }

    /// <summary>
    /// The file or directory to analyze.
    /// </summary>
    public required string Path
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, suppresses the version banner, progress UI, and the
    /// "Saved to" message, leaving only the result output.
    /// </summary>
    public bool Quiet
    {
        get; init;
    }

    /// <summary>
    /// Whether to honor <c>.gitignore</c> files discovered under the scan root.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool RespectGitignore { get; init; } = true;

    /// <summary>
    /// The key by which the per-language summary is ordered.
    /// </summary>
    public LanguageSort Sort { get; init; } = LanguageSort.Total;

    /// <summary>
    /// When set, keeps only the first this-many languages in the summary after sorting.
    /// </summary>
    public int? Top
    {
        get; init;
    }
}

/// <summary>
/// Orchestrates an analysis run: scan files, analyze each one, aggregate the
/// results, and render them.
/// </summary>
public sealed class AnalyzeHandler
{
    private const string StdoutToken = "-";
    private static readonly TimeSpan ScanStatusRefreshInterval = TimeSpan.FromMilliseconds(300);

    private readonly FileAnalyzer _analyzer = new();
    private readonly DirectoryScanner _scanner = new();

    /// <summary>
    /// Runs the analysis described by <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <returns>
    /// A process exit code: 0 on success, non-zero on error.
    /// </returns>
    public int Execute(AnalyzeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var version = typeof(AnalyzeHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (options.Format == OutputFormat.Table && !Console.IsOutputRedirected && !options.Quiet)
        {
            if (!string.IsNullOrEmpty(version))
            {
                AnsiConsole.MarkupLine($"[grey]sloc {Markup.Escape(version)}[/]");
            }
        }

        if (!options.NoUpdateCheck && !options.Quiet && !string.IsNullOrEmpty(version))
        {
            CheckForUpdate(version);
        }

        // Spectre's live table / progress bar drive the console cursor, which throws when
        // stdout is redirected (pipes, CI, files). Suppress it there and when the caller
        // asked for a quiet / no-progress run.
        var showProgress = !Console.IsOutputRedirected && !options.Quiet && !options.NoProgress;

        var scanOptions = new ScanOptions
        {
            Includes = options.Includes,
            Excludes = options.Excludes,
            Recursive = !options.NoRecursive,
            IncludeUnknown = options.IncludeUnknown,
            RespectGitignore = options.RespectGitignore
        };

        ScanResult scanResult;
        try
        {
            if (showProgress)
            {
                ScanResult? result = null;
                var refreshTimer = Stopwatch.StartNew();
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .Start("Scanning files...", ctx =>
                    {
                        result = _scanner.Scan(
                            options.Path,
                            scanOptions,
                            onFileFound: (count, path) =>
                            {
                                if (refreshTimer.Elapsed < ScanStatusRefreshInterval)
                                {
                                    return;
                                }

                                refreshTimer.Restart();
                                ctx.Status($"Scanning... [green]{count:N0}[/] files ([grey]{Markup.Escape(Path.GetFileName(path))}[/])");
                            },
                            onGitignoreScan: (count, path) =>
                            {
                                if (refreshTimer.Elapsed < ScanStatusRefreshInterval)
                                {
                                    return;
                                }

                                refreshTimer.Restart();
                                ctx.Status($"Scanning... checking .gitignore ([green]{count:N0}[/] dirs, [grey]{Markup.Escape(Path.GetFileName(path))}[/])");
                            });
                    });
                scanResult = result ?? throw new InvalidOperationException("Scan did not complete.");
            }
            else
            {
                scanResult = _scanner.Scan(options.Path, scanOptions);
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var files = scanResult.Files;
        var skipped = new List<SkippedEntry>(scanResult.Skipped);

        // Analyze into fixed slots so the merged order is deterministic (scan order),
        // independent of the degree of parallelism.
        var analyses = new FileAnalysis?[files.Count];
        var fileSkips = new SkippedEntry?[files.Count];
        var aggregator = new LiveAggregator();

        void AnalyzeAt(int i)
        {
            try
            {
                var analysis = _analyzer.Analyze(files[i].Path, files[i].Language);
                analyses[i] = analysis;
                aggregator.Add(analysis);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BinaryFileException)
            {
                fileSkips[i] = new SkippedEntry(files[i].Path, ex.Message);
            }
        }

        var jobs = options.Jobs is { } requested && requested > 0 ? requested : Environment.ProcessorCount;
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = jobs };

        if (files.Count == 0)
        {
            // Nothing to analyze.
        }
        else if (!showProgress)
        {
            Parallel.For(0, files.Count, parallelOptions, AnalyzeAt);
        }
        else if (options is { Format: OutputFormat.Table, ByFile: false, BaselinePath: null })
        {
            AnsiConsole.Live(TableRenderer.BuildLanguageTable(aggregator.ToSummary(), noHealth: options.NoHealth))
                .AutoClear(false)
                .Start(ctx =>
                {
                    // Analyze on a background task while this thread refreshes the table
                    // from the thread-safe aggregator (no per-tick re-aggregation).
                    var work = Task.Run(() => Parallel.For(0, files.Count, parallelOptions, AnalyzeAt));
                    while (!work.IsCompleted)
                    {
                        ctx.UpdateTarget(TableRenderer.BuildLanguageTable(
                            aggregator.ToSummary(),
                            $"[grey]Analyzing... {aggregator.FilesProcessed:N0} / {files.Count:N0}[/]",
                            noHealth: options.NoHealth));
                        Thread.Sleep(100);
                    }

                    work.GetAwaiter().GetResult();
                    ctx.UpdateTarget(TableRenderer.BuildLanguageTable(aggregator.ToSummary(), noHealth: options.NoHealth));
                });
        }
        else
        {
            AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var task = ctx.AddTask("[green]Analyzing[/]", maxValue: files.Count);
                    var work = Task.Run(() => Parallel.For(0, files.Count, parallelOptions, i =>
                    {
                        AnalyzeAt(i);
                        task.Increment(1);
                    }));
                    work.GetAwaiter().GetResult();
                });
        }

        // Merge in scan order so results are deterministic regardless of --jobs.
        var results = new List<FileAnalysis>(files.Count);
        foreach (var analysis in analyses)
        {
            if (analysis is not null)
            {
                results.Add(analysis);
            }
        }

        foreach (var fileSkip in fileSkips)
        {
            if (fileSkip is not null)
            {
                skipped.Add(fileSkip);
            }
        }

        var summary = new AnalysisSummary(
            results,
            skipped,
            options.Sort,
            descending: options.Sort != LanguageSort.Name,
            top: options.Top);

        if (options.BaselinePath is { } baselinePath)
        {
            JsonReport baseline;
            try
            {
                baseline = DiffRenderer.Load(baselinePath);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"sloc: {ex.Message}");
                return ExitCode.Error;
            }

            if (options.Format == OutputFormat.Json && (options.OutputFile is null || options.OutputFile == StdoutToken))
            {
                DiffRenderer.RenderJson(Console.Out, summary, baseline);
            }
            else if (options.Format == OutputFormat.Json)
            {
                WriteToFile(options.OutputFile!, writer => DiffRenderer.RenderJson(writer, summary, baseline), options.Quiet);
            }
            else
            {
                DiffRenderer.RenderTable(summary, baseline);
            }

            return ThresholdResult(options, summary);
        }

        if (options.Format == OutputFormat.Json)
        {
            // JSON defaults to stdout (pipeable); an explicit path writes a file.
            if (options.OutputFile is null || options.OutputFile == StdoutToken)
            {
                new JsonRenderer(Console.Out).Render(summary, options.ByFile, options.NoHealth);
            }
            else
            {
                WriteToFile(options.OutputFile, writer => new JsonRenderer(writer).Render(summary, options.ByFile, options.NoHealth), options.Quiet);
            }
        }
        else if (options.Format == OutputFormat.Html)
        {
            if (options.OutputFile == StdoutToken)
            {
                new HtmlRenderer(Console.Out).Render(summary, options.ByFile, options.NoHealth);
            }
            else
            {
                WriteToFile(options.OutputFile ?? "sloc-report.html", writer => new HtmlRenderer(writer).Render(summary, options.ByFile, options.NoHealth), options.Quiet);
            }
        }
        else
        {
            if (summary.FileCount == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files matched.[/]");
            }
            else if (options.ByFile)
            {
                TableRenderer.RenderByFile(summary, options.NoHealth, options.Paged);
            }
            else if (!showProgress)
            {
                // The live table only renders during progress; render it here otherwise.
                AnsiConsole.Write(TableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth));
            }

            TableRenderer.RenderSkipped(summary);
        }

        return ThresholdResult(options, summary);
    }

    private static void CheckForUpdate(string currentVersion)
    {
        try
        {
            var result = new UpdateChecker()
                .CheckForUpdateAsync(currentVersion, TimeSpan.FromSeconds(2), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (result is not null)
            {
                Console.Error.WriteLine(
                    $"A new version of sloc is available: {result.LatestVersion} (current: {currentVersion})");
                Console.Error.WriteLine($"Download: {result.ReleaseUrl}");
            }
        }
        catch
        {
            // An update check must never break or delay a normal analysis run.
        }
    }

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

    private static void WriteToFile(string path, Action<TextWriter> render, bool quiet)
    {
        // UTF-8 without a BOM so piped/consumed files (e.g. via jq) parse cleanly.
        using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            render(writer);
        }

        if (!quiet)
        {
            AnsiConsole.MarkupLine($"[green]Saved to:[/] {Markup.Escape(path)}");
        }
    }

    /// <summary>
    /// Thread-safe incremental aggregator of per-language counts, used to refresh the
    /// live table without re-aggregating every analyzed file on each tick.
    /// </summary>
    private sealed class LiveAggregator
    {
        private readonly Dictionary<string, Counts> _byLanguage = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private int _files;

        public int FilesProcessed
        {
            get
            {
                lock (_gate)
                {
                    return _files;
                }
            }
        }

        public void Add(FileAnalysis analysis)
        {
            lock (_gate)
            {
                _files++;
                _byLanguage.TryGetValue(analysis.Language, out var counts);
                _byLanguage[analysis.Language] = new Counts(
                    counts.Files + 1,
                    counts.Code + analysis.Code,
                    counts.Comment + analysis.Comment,
                    counts.Blank + analysis.Blank);
            }
        }

        public AnalysisSummary ToSummary()
        {
            lock (_gate)
            {
                var byLanguage = _byLanguage
                    .Select(entry => new LanguageStatistics
                    {
                        Language = entry.Key,
                        Files = entry.Value.Files,
                        Code = entry.Value.Code,
                        Comment = entry.Value.Comment,
                        Blank = entry.Value.Blank
                    })
                    .OrderByDescending(stats => stats.Total)
                    .ThenBy(stats => stats.Language, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new AnalysisSummary(byLanguage, _files);
            }
        }

        private readonly record struct Counts(int Files, int Code, int Comment, int Blank);
    }
}