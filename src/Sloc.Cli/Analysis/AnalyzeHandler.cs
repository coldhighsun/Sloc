using Sloc.Cli.Output;
using Sloc.Cli.Updates;
using Sloc.Core;
using Sloc.Core.Models;
using Sloc.Core.Scanning;
using Spectre.Console;
using System.Diagnostics;
using System.Reflection;

namespace Sloc.Cli.Analysis;

/// <summary>
/// Orchestrates an analysis run: scan files, analyze each one, aggregate the
/// results, and render them.
/// </summary>
public sealed class AnalyzeHandler
{
    private const string StdoutToken = "-";
    private static readonly TimeSpan LiveTableRefreshInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ScanStatusRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WatchDebounceInterval = TimeSpan.FromMilliseconds(400);
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

        if (options.Format == OutputFormat.Table && !Console.IsOutputRedirected && !options.Quiet)
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

        var tableRenderer = new TableRenderer();

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

        if (options.Watch)
        {
            return ExecuteWatch(options, scanOptions, tableRenderer, sourcePath);
        }

        GitSnapshot? gitSnapshot = null;
        if (options.GitHash is { } gitHash)
        {
            try
            {
                gitSnapshot = new GitSnapshotExtractor().Extract(options.Path, gitHash);
            }
            catch (Exception ex) when (ex is GitSnapshotException or IOException or UnauthorizedAccessException)
            {
                // GitSnapshotExtractor.Extract cleans up its own temp directory on any
                // failure, including these, before rethrowing.
                Console.Error.WriteLine($"sloc: {ex.Message}");
                return ExitCode.Error;
            }
        }

        try
        {
            ScanResult scanResult;
            try
            {
                if (gitSnapshot is not null)
                {
                    scanResult = _scanner.ScanFiles(gitSnapshot.Files.Select(f => f.TempPath), scanOptions);
                }
                else if (options.ListFile is { } listFile)
                {
                    scanResult = _scanner.ScanFiles(ReadListFile(listFile), scanOptions);
                }
                else if (showProgress)
                {
                    ScanResult? result = null;
                    var refreshTimer = Stopwatch.StartNew();
                    var gitignoreScanLabel = (scanOptions.RespectGitignore, scanOptions.RespectGitAttributes) switch
                    {
                        (true, true) => "checking .gitignore/.gitattributes",
                        (true, false) => "checking .gitignore",
                        (false, true) => "checking .gitattributes",
                        (false, false) => "walking directories",
                    };

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
                                    ctx.Status($"Scanning... {gitignoreScanLabel} ([green]{count:N0}[/] dirs, [grey]{Markup.Escape(Path.GetFileName(path))}[/])");
                                });
                        });
                    scanResult = result ?? throw new InvalidOperationException("Scan did not complete.");
                }
                else
                {
                    scanResult = _scanner.Scan(options.Path, scanOptions);
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }

            var files = scanResult.Files;
            var skipped = new List<SkippedEntry>(scanResult.Skipped);

            // Analyze into fixed slots so the merged order is deterministic (scan order),
            // independent of the degree of parallelism.
            var analyses = new FileAnalysis?[files.Count];
            var fileSkips = new SkippedEntry?[files.Count];
            var aggregator = new LiveAggregator(options.Sort, options.Top);

            void AnalyzeAt(int i)
            {
                try
                {
                    var analysis = _analyzer.Analyze(files[i].Path, files[i].Language, computeHash: options.Unique);
                    analyses[i] = analysis;
                    aggregator.Add(analysis);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BinaryFileException)
                {
                    fileSkips[i] = new SkippedEntry(files[i].Path, ex.Message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    // Any other per-file failure (e.g. a decoding error) skips just that file
                    // rather than aborting the whole run; fatal conditions are left to propagate.
                    fileSkips[i] = new SkippedEntry(files[i].Path, $"analysis error: {ex.Message}");
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
                AnsiConsole.Live(tableRenderer.BuildLanguageTable(aggregator.ToSummary(), noHealth: options.NoHealth, noComplexity: options.NoComplexity))
                    .AutoClear(false)
                    .Start(ctx =>
                    {
                        // Analyze on a background task while this thread refreshes the table
                        // from the thread-safe aggregator (no per-tick re-aggregation).
                        var work = Task.Run(() => Parallel.For(0, files.Count, parallelOptions, AnalyzeAt));
                        while (!work.IsCompleted)
                        {
                            ctx.UpdateTarget(tableRenderer.BuildLanguageTable(
                                aggregator.ToSummary(),
                                $"[grey]Analyzing... {aggregator.FilesProcessed:N0} / {files.Count:N0}[/]",
                                noHealth: options.NoHealth,
                                noComplexity: options.NoComplexity));
                            Thread.Sleep(LiveTableRefreshInterval);
                        }

                        work.GetAwaiter().GetResult();
                        ctx.UpdateTarget(tableRenderer.BuildLanguageTable(aggregator.ToSummary(), noHealth: options.NoHealth, noComplexity: options.NoComplexity));
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

            if (options.Unique)
            {
                results = DeduplicateByHash(results, skipped);
            }

            if (gitSnapshot is not null)
            {
                var gitPathByTempPath = gitSnapshot.Files.ToDictionary(f => f.TempPath, f => f.GitPath);
                results = RemapGitPaths(results, gitPathByTempPath);
                skipped = RemapGitPaths(skipped, gitPathByTempPath);
                skipped.AddRange(gitSnapshot.Skipped);
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

                // Baseline diffs are only rendered as a console Table or as JSON. Any other
                // requested format (and any --output for a non-JSON diff) is not supported, so
                // warn and fall back to the Table rather than silently ignoring the request.
                if (options.Format is not (OutputFormat.Json or OutputFormat.Table))
                {
                    if (options.OutputFile is not null && options.OutputFile != StdoutToken)
                    {
                        Console.Error.WriteLine(
                            $"sloc: --baseline diff output is only supported for Table and Json formats; -f {options.Format.ToString().ToLowerInvariant()} with -o '{options.OutputFile}' cannot be honored.");
                        return ExitCode.Error;
                    }

                    Console.Error.WriteLine(
                        $"sloc: --baseline diff output is only supported for Table and Json formats; ignoring -f {options.Format.ToString().ToLowerInvariant()} and rendering a table.");
                }

                if (options.Format == OutputFormat.Json && (options.OutputFile is null || options.OutputFile == StdoutToken))
                {
                    DiffRenderer.RenderJson(Console.Out, summary, baseline);
                }
                else if (options.Format == OutputFormat.Json)
                {
                    if (!WriteToFile(options.OutputFile!, writer => DiffRenderer.RenderJson(writer, summary, baseline), options.Quiet))
                    {
                        return ExitCode.Error;
                    }
                }
                else
                {
                    DiffRenderer.RenderTable(summary, baseline);
                }

                ReportUpdate(updateCheck, version);
                return ThresholdResult(options, summary);
            }

            if (CreateRenderer(options.Format) is { } createRenderer)
            {
                // These formats default to stdout (pipeable/pasteable); an explicit path writes a file.
                if (options.OutputFile is null || options.OutputFile == StdoutToken)
                {
                    createRenderer(Console.Out).Render(summary, options.ByFile, options.NoHealth, options.Detailed, sourcePath, options.NoComplexity);
                }
                else
                {
                    if (!WriteToFile(options.OutputFile, writer => createRenderer(writer).Render(summary, options.ByFile, options.NoHealth, options.Detailed, sourcePath, options.NoComplexity), options.Quiet))
                    {
                        return ExitCode.Error;
                    }
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
                    tableRenderer.RenderByFile(summary, options.NoHealth, options.Paged, options.NoComplexity);
                }
                else if (!showProgress)
                {
                    // The live table only renders during progress; render it here otherwise.
                    AnsiConsole.Write(tableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth, noComplexity: options.NoComplexity));
                }

                tableRenderer.RenderSkipped(summary);
            }

            ReportUpdate(updateCheck, version);
            return ThresholdResult(options, summary);
        }
        finally
        {
            gitSnapshot?.Dispose();
        }
    }

    /// <summary>
    /// Runs a watch loop: renders an initial table, then watches <paramref name="options"/>'s
    /// path for file-system changes, debouncing bursts of events and re-running the scan +
    /// analysis on each settled change, refreshing the same live table. Runs until the user
    /// presses Ctrl+C.
    /// </summary>
    /// <remarks>
    /// This deliberately re-implements a small, self-contained scan+analyze+summarize pass
    /// (<see cref="RunWatchPass"/>) rather than reusing the one-shot pipeline in
    /// <see cref="Execute"/>, whose progress-bar/spinner/git-snapshot/baseline branches are
    /// not relevant here (watch is Table-only and excludes --git-hash/--list-file/--baseline)
    /// and would add risk to entangle with a repeatedly-run loop.
    /// </remarks>
    private int ExecuteWatch(AnalyzeOptions options, ScanOptions scanOptions, TableRenderer tableRenderer, string sourcePath)
    {
        if (!options.Quiet)
        {
            var version = typeof(AnalyzeHandler).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrEmpty(version))
            {
                AnsiConsole.MarkupLine($"[grey]sloc {Markup.Escape(version)}[/]");
            }

            AnsiConsole.MarkupLine($"[grey]Watching: {Markup.Escape(sourcePath)}[/]");
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");
        }

        var watchDir = Directory.Exists(options.Path) ? options.Path : Path.GetDirectoryName(Path.GetFullPath(options.Path)) ?? ".";

        var cancellationRequested = false;
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            var pendingChange = 0;
            using var watcher = new FileSystemWatcher(watchDir)
            {
                IncludeSubdirectories = !options.NoRecursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
            };

            void OnChange(object sender, FileSystemEventArgs e) => Interlocked.Exchange(ref pendingChange, 1);
            watcher.Changed += OnChange;
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.EnableRaisingEvents = true;

            AnsiConsole.Live(tableRenderer.BuildLanguageTable(RunWatchPass(options, scanOptions), noHealth: options.NoHealth, noComplexity: options.NoComplexity))
                .AutoClear(false)
                .Start(ctx =>
                {
                    while (!cancellationRequested)
                    {
                        Thread.Sleep(WatchDebounceInterval);

                        if (cancellationRequested)
                        {
                            break;
                        }

                        if (Interlocked.Exchange(ref pendingChange, 0) == 0)
                        {
                            continue;
                        }

                        var summary = RunWatchPass(options, scanOptions);
                        ctx.UpdateTarget(tableRenderer.BuildLanguageTable(
                            summary,
                            $"[grey]Last update: {DateTime.Now:T}[/]",
                            noHealth: options.NoHealth,
                            noComplexity: options.NoComplexity));
                    }
                });
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        return ExitCode.Success;

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellationRequested = true;
        }
    }

    /// <summary>
    /// Scans and analyzes once for <see cref="ExecuteWatch"/>: full re-scan (applying the
    /// same include/exclude/gitignore/gitattributes/language filters as a normal run),
    /// parallel per-file analysis, and aggregation into an <see cref="AnalysisSummary"/>.
    /// </summary>
    private AnalysisSummary RunWatchPass(AnalyzeOptions options, ScanOptions scanOptions)
    {
        var scanResult = _scanner.Scan(options.Path, scanOptions);
        var files = scanResult.Files;
        var skipped = new List<SkippedEntry>(scanResult.Skipped);

        var analyses = new FileAnalysis?[files.Count];
        var fileSkips = new SkippedEntry?[files.Count];

        void AnalyzeAt(int i)
        {
            try
            {
                analyses[i] = _analyzer.Analyze(files[i].Path, files[i].Language, computeHash: options.Unique);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BinaryFileException)
            {
                fileSkips[i] = new SkippedEntry(files[i].Path, ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                fileSkips[i] = new SkippedEntry(files[i].Path, $"analysis error: {ex.Message}");
            }
        }

        var jobs = options.Jobs is { } requested && requested > 0 ? requested : Environment.ProcessorCount;
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, AnalyzeAt);

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

        if (options.Unique)
        {
            results = DeduplicateByHash(results, skipped);
        }

        return new AnalysisSummary(
            results,
            skipped,
            options.Sort,
            descending: options.Sort != LanguageSort.Name,
            top: options.Top);
    }

    /// <summary>
    /// Returns a factory for the <see cref="IResultRenderer"/> matching <paramref name="format"/>,
    /// or <see langword="null"/> for <see cref="OutputFormat.Table"/> (rendered separately, since
    /// it writes directly to the console rather than through a <see cref="TextWriter"/>). A
    /// factory (rather than a single instance) is returned so the same format can be rendered
    /// twice with different writers, e.g. once to stdout and once to a file.
    /// </summary>
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

            skipped.Add(new SkippedEntry(analysis.Path, $"duplicate of {firstPath}"));
        }

        return unique;
    }

    private static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

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
    /// git-relative path.
    /// </summary>
    private static List<SkippedEntry> RemapGitPaths(List<SkippedEntry> skipped, Dictionary<string, string> gitPathByTempPath)
    {
        for (var i = 0; i < skipped.Count; i++)
        {
            if (gitPathByTempPath.TryGetValue(skipped[i].Path, out var gitPath))
            {
                skipped[i] = skipped[i] with
                {
                    Path = gitPath
                };
            }
        }

        return skipped;
    }

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