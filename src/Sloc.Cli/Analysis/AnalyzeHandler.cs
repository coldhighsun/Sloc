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
/// results, and render them. The stateless scan/analyze/render plumbing this class
/// delegates to lives in the other half of this partial class, <c>AnalyzeHandler.Support.cs</c>.
/// </summary>
public sealed partial class AnalyzeHandler
{
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

        var ctx = BuildRunContext(options);

        if (options.Watch)
        {
            return ExecuteWatch(options, ctx.ScanOptions, ctx.TableRenderer, ctx.SourcePath);
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
                scanResult = RunScan(gitSnapshot, options, ctx.ScanOptions, ctx.ShowProgress);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }

            var files = scanResult.Files;
            var skipped = new List<SkippedEntry>(scanResult.Skipped);

            AnalysisSummary Summarize(List<FileAnalysis> results)
            {
                if (gitSnapshot is not null)
                {
                    var gitPathByTempPath = gitSnapshot.Files.ToDictionary(f => f.TempPath, f => f.GitPath);
                    results = RemapGitPaths(results, gitPathByTempPath);
                    skipped = RemapGitPaths(skipped, gitPathByTempPath);
                    skipped.AddRange(gitSnapshot.Skipped);
                }

                // Diffing needs every language present in either side to compare correctly, so
                // --top must not truncate the current-side summary when a diff is requested
                // (matches AnalyzeGitRef's baseline-side summary, which is never top-limited
                // either); --top is applied here only for a normal (non-diff) render.
                var isDiffing = options.BaselinePath is not null || options.CompareTo is not null;
                return new AnalysisSummary(
                    results,
                    skipped,
                    options.Sort,
                    descending: options.Sort != LanguageSort.Name,
                    top: isDiffing ? null : options.Top);
            }

            var summary = files.Count == 0
                ? Summarize([])
                : AnalyzeWithUi(files, options, skipped, ctx.ShowProgress, ctx.TableRenderer, Summarize);

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

                return RenderDiffAndFinish(
                    summary, "--baseline", options, ctx.UpdateCheck, ctx.Version,
                    writer => DiffRenderer.RenderJson(writer, summary, baseline),
                    () => DiffRenderer.RenderTable(summary, baseline));
            }

            if (options.CompareTo is { } compareToRef)
            {
                AnalysisSummary compareBaseline;
                try
                {
                    compareBaseline = AnalyzeGitRef(options.Path, compareToRef, ctx.ScanOptions, options);
                }
                catch (Exception ex) when (ex is GitSnapshotException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"sloc: {ex.Message}");
                    return ExitCode.Error;
                }

                return RenderDiffAndFinish(
                    summary, "--compare-to", options, ctx.UpdateCheck, ctx.Version,
                    writer => DiffRenderer.RenderJson(writer, summary, compareBaseline),
                    () => DiffRenderer.RenderTable(summary, compareBaseline));
            }

            var renderExit = RenderNormal(summary, options, ctx.SourcePath, ctx.TableRenderer, ctx.ShowProgress);
            if (renderExit != ExitCode.Success)
            {
                return renderExit;
            }

            ReportUpdate(ctx.UpdateCheck, ctx.Version);
            return ThresholdResult(options, summary);
        }
        finally
        {
            gitSnapshot?.Dispose();
        }
    }

    /// <summary>
    /// Analyzes <paramref name="files"/> in parallel (respecting <see cref="AnalyzeOptions.Jobs"/>),
    /// merges results in scan order so output is deterministic regardless of the degree of
    /// parallelism, appends any per-file failures to <paramref name="skipped"/>, and applies
    /// <see cref="AnalyzeOptions.Unique"/> deduplication. Shared by the one-shot pipeline in
    /// <see cref="Execute"/> and the repeated passes in <see cref="ExecuteWatch"/> so per-file
    /// error handling and merge/dedup behavior can't drift between the two.
    /// </summary>
    /// <param name="files">The files to analyze, in scan order.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="skipped">The skipped-entries list per-file failures are appended to.</param>
    /// <param name="onFileAnalyzed">
    /// Optional callback invoked (from a worker thread, once per file) with the file's index
    /// and its analysis, or <see langword="null"/> if that file was skipped. Used to drive
    /// progress UI (a live-aggregated table or a progress bar) while analysis runs.
    /// </param>
    private List<FileAnalysis> AnalyzeFiles(
        IReadOnlyList<ScannedFile> files,
        AnalyzeOptions options,
        List<SkippedEntry> skipped,
        Action<int, FileAnalysis?>? onFileAnalyzed = null)
    {
        // Analyze into fixed slots so the merged order is deterministic (scan order),
        // independent of the degree of parallelism.
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
                // Any other per-file failure (e.g. a decoding error) skips just that file
                // rather than aborting the whole run; fatal conditions are left to propagate.
                fileSkips[i] = new SkippedEntry(files[i].Path, $"analysis error: {ex.Message}");
            }

            onFileAnalyzed?.Invoke(i, analyses[i]);
        }

        var jobs = options.Jobs is { } requested && requested > 0 ? requested : Environment.ProcessorCount;
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, AnalyzeAt);

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

        return options.Unique ? DeduplicateByHash(results, skipped) : results;
    }

    /// <summary>
    /// Extracts <paramref name="repoPath"/>'s repository tree as of <paramref name="gitRef"/>
    /// via <see cref="GitSnapshotExtractor"/>, then scans and analyzes it with
    /// <paramref name="scanOptions"/> and remaps results back to git-relative paths. Used by
    /// <c>--compare-to</c> to build the baseline side of a diff without requiring a previously
    /// saved report. The temporary extraction directory is always cleaned up before returning.
    /// </summary>
    /// <remarks>
    /// When <see cref="AnalyzeOptions.GitHash"/> is not set, the "current" side being diffed
    /// against is a normal filesystem scan scoped to <paramref name="repoPath"/>, so the
    /// baseline is restricted to that same subtree — otherwise the diff would compare a
    /// scoped current summary against an unscoped (whole-repository) baseline. But when
    /// <see cref="AnalyzeOptions.GitHash"/> is set, the current side is itself unscoped (per
    /// <c>--git-hash</c>'s own "<paramref name="repoPath"/> is the repo root" convention, it
    /// always analyzes the whole repository regardless of <paramref name="repoPath"/>), so the
    /// baseline must stay unscoped too, to match.
    /// </remarks>
    /// <param name="repoPath">The path passed as <c>path</c>; the repo root or one of its subdirectories.</param>
    /// <param name="gitRef">The commit/tree-ish to extract and analyze.</param>
    /// <param name="scanOptions">The scan options to apply to the extracted snapshot.</param>
    /// <param name="options">The parsed options.</param>
    private AnalysisSummary AnalyzeGitRef(string repoPath, string gitRef, ScanOptions scanOptions, AnalyzeOptions options)
    {
        using var snapshot = new GitSnapshotExtractor().Extract(repoPath, gitRef);

        var filesInScope = snapshot.Files;
        IEnumerable<SkippedEntry> skippedInScope = snapshot.Skipped;
        if (options.GitHash is null && snapshot.RelativePrefix.Length > 0)
        {
            var relativePrefix = snapshot.RelativePrefix;
            bool InScope(string gitPath) =>
                gitPath.Equals(relativePrefix, StringComparison.Ordinal)
                    || gitPath.StartsWith(relativePrefix + "/", StringComparison.Ordinal);

            filesInScope = snapshot.Files.Where(f => InScope(f.GitPath)).ToList();
            skippedInScope = snapshot.Skipped.Where(s => InScope(s.Path));
        }

        var scanResult = _scanner.ScanFiles(filesInScope.Select(f => f.TempPath), scanOptions);
        var skipped = new List<SkippedEntry>(scanResult.Skipped);
        var results = AnalyzeFiles(scanResult.Files, options, skipped);

        var gitPathByTempPath = filesInScope.ToDictionary(f => f.TempPath, f => f.GitPath);
        results = RemapGitPaths(results, gitPathByTempPath);
        skipped = RemapGitPaths(skipped, gitPathByTempPath);
        skipped.AddRange(skippedInScope);

        // No --top limit here: the diff needs every language present in either side to
        // compare correctly, regardless of how the current run's summary was truncated.
        return new AnalysisSummary(results, skipped, options.Sort, descending: options.Sort != LanguageSort.Name);
    }

    /// <summary>
    /// Runs <see cref="AnalyzeFiles"/> for a one-shot run, wrapped in whichever progress UI
    /// applies: no UI (quiet/redirected/no-progress), a live-refreshed language table (Table
    /// format, not <c>--by-file</c>, no <c>--baseline</c>/<c>--compare-to</c>) driven by a <see cref="LiveAggregator"/>,
    /// or a plain progress bar otherwise. <paramref name="files"/> is known non-empty; the
    /// empty case is handled by the caller.
    /// </summary>
    /// <param name="files">The (known non-empty) files to analyze.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="skipped">The skipped-entries list per-file failures are appended to.</param>
    /// <param name="showProgress">Whether progress UI may be drawn.</param>
    /// <param name="tableRenderer">The table renderer used to build the live-refreshed language table.</param>
    /// <param name="summarize">
    /// Builds the run's final summary from the analysis results. Called once, before the
    /// live table's last frame is drawn, so that frame shows exactly that summary.
    /// </param>
    private AnalysisSummary AnalyzeWithUi(
        IReadOnlyList<ScannedFile> files,
        AnalyzeOptions options,
        List<SkippedEntry> skipped,
        bool showProgress,
        TableRenderer tableRenderer,
        Func<List<FileAnalysis>, AnalysisSummary> summarize)
    {
        if (!showProgress)
        {
            return summarize(AnalyzeFiles(files, options, skipped));
        }

        // A diff (--baseline/--compare-to) renders its own table afterwards, so the live
        // language table would just be left behind above it.
        if (options is not { Format: OutputFormat.Table, ByFile: false, BaselinePath: null, CompareTo: null })
        {
            var progressResults = new List<FileAnalysis>();

            AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var task = ctx.AddTask("[green]Analyzing[/]", maxValue: files.Count);
                    var work = Task.Run(() => progressResults = AnalyzeFiles(files, options, skipped, (_, _) => task.Increment(1)));
                    work.GetAwaiter().GetResult();
                });

            return summarize(progressResults);
        }

        var aggregator = new LiveAggregator(options.Sort, options.Top, unique: options.Unique);
        var results = new List<FileAnalysis>();
        AnalysisSummary? summary = null;

        AnsiConsole.Live(tableRenderer.BuildLanguageTable(aggregator.ToSummary(), noHealth: options.NoHealth, noComplexity: options.NoComplexity))
            .AutoClear(false)
            .Start(ctx =>
            {
                // Analyze on a background task while this thread refreshes the table
                // from the thread-safe aggregator (no per-tick re-aggregation).
                var work = Task.Run(() => results = AnalyzeFiles(files, options, skipped, (_, analysis) =>
                {
                    if (analysis is not null)
                    {
                        aggregator.Add(analysis);
                    }
                }));
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

                // The last frame stays on screen as the run's language table (RenderNormal
                // doesn't draw it again), so draw the run's actual summary rather than the
                // aggregator's running approximation (e.g. which of --unique's duplicates got
                // counted can differ, since files finish out of scan order).
                summary = summarize(results);
                ctx.UpdateTarget(tableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth, noComplexity: options.NoComplexity));
            });

        return summary ?? throw new InvalidOperationException("Analysis did not complete.");
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
    /// <param name="options">The parsed options.</param>
    /// <param name="scanOptions">The scan options to apply on every pass.</param>
    /// <param name="tableRenderer">The table renderer used to build the live-refreshed language table.</param>
    /// <param name="sourcePath">The resolved source path to surface in the startup banner.</param>
    private int ExecuteWatch(AnalyzeOptions options, ScanOptions scanOptions, TableRenderer tableRenderer, string sourcePath)
    {
        // Same error a one-shot run reports, before FileSystemWatcher can fail on it instead.
        if (!Directory.Exists(options.Path) && !File.Exists(options.Path))
        {
            Console.Error.WriteLine($"Path not found: {options.Path}");
            return ExitCode.Error;
        }

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
        // Signaled immediately on Ctrl+C so the debounce wait below wakes up right away,
        // instead of a plain Thread.Sleep leaving Ctrl+C waiting out the rest of the interval.
        using var cancelSignal = new ManualResetEventSlim(false);
        Console.CancelKeyPress += OnCancelKeyPress;

        AnalysisSummary? lastSummary = null;
        var lastPassFailed = false;
        FileSystemWatcher? watcher = null;

        try
        {
            var pendingChange = 0;

            FileSystemWatcher StartWatcher()
            {
                var started = new FileSystemWatcher(watchDir)
                {
                    IncludeSubdirectories = !options.NoRecursive,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                };

                started.Changed += OnChange;
                started.Created += OnChange;
                started.Deleted += OnChange;
                started.Renamed += OnChange;
                // An internal buffer overflow means events were dropped; rescan to catch up.
                started.Error += (_, _) => Interlocked.Exchange(ref pendingChange, 1);
                try
                {
                    started.EnableRaisingEvents = true;
                }
                catch
                {
                    started.Dispose();
                    throw;
                }

                return started;
            }

            void OnChange(object sender, FileSystemEventArgs e)
            {
                // Best-effort noise reduction: skip the well-known build/VCS/package
                // directories the scanner itself always excludes by default. This is not a
                // full replay of --include/--exclude/.gitignore filtering (which would mean
                // duplicating DirectoryScanner's glob/gitignore matching here); other filtered
                // files can still trigger a rescan, which is harmless since rescans are
                // idempotent, just more frequent than the exact watched file set.
                if (IsInIgnoredDirectory(watchDir, e.FullPath))
                {
                    return;
                }

                Interlocked.Exchange(ref pendingChange, 1);
            }

            watcher = StartWatcher();

            try
            {
                lastSummary = RunWatchPass(options, scanOptions);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }

            AnsiConsole.Live(tableRenderer.BuildLanguageTable(lastSummary, noHealth: options.NoHealth, noComplexity: options.NoComplexity))
                .AutoClear(true)
                .Start(ctx =>
                {
                    ctx.Refresh();

                    while (!cancellationRequested)
                    {
                        cancelSignal.Wait(WatchDebounceInterval);

                        if (cancellationRequested)
                        {
                            break;
                        }

                        // The watched directory was deleted, which leaves its watcher dead:
                        // poll for it to be recreated, then watch it afresh and rescan.
                        if (watcher is null)
                        {
                            if (!Directory.Exists(watchDir))
                            {
                                continue;
                            }

                            try
                            {
                                watcher = StartWatcher();
                            }
                            catch (Exception ex) when (ex is ArgumentException or IOException)
                            {
                                // Deleted again before it could be watched; keep polling.
                                continue;
                            }

                            Interlocked.Exchange(ref pendingChange, 1);
                        }

                        if (Interlocked.Exchange(ref pendingChange, 0) == 0)
                        {
                            continue;
                        }

                        string caption;
                        try
                        {
                            lastSummary = RunWatchPass(options, scanOptions);
                            lastPassFailed = false;
                            caption = $"[grey]Last update: {DateTime.Now:T}[/]";
                        }
                        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                        {
                            // E.g. the watched directory was deleted or became unreadable:
                            // keep the last good table on screen instead of ending the watch.
                            lastPassFailed = true;
                            if (!Directory.Exists(watchDir))
                            {
                                watcher?.Dispose();
                                watcher = null;
                                caption = $"[yellow]{Markup.Escape(watchDir)} was removed at {DateTime.Now:T}; waiting for it to reappear.[/]";
                            }
                            else
                            {
                                caption = $"[yellow]Rescan failed at {DateTime.Now:T}: {Markup.Escape(ex.Message)}[/]";
                            }
                        }

                        ctx.UpdateTarget(tableRenderer.BuildLanguageTable(
                            lastSummary,
                            caption,
                            noHealth: options.NoHealth,
                            noComplexity: options.NoComplexity));
                    }
                });
        }
        finally
        {
            watcher?.Dispose();
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        return WatchExitCode(options, lastSummary, lastPassFailed);

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellationRequested = true;
            cancelSignal.Set();
        }
    }

    /// <summary>
    /// Renders a diff (<paramref name="renderJson"/>/<paramref name="renderTable"/>) the same
    /// way regardless of whether the baseline came from <c>--baseline</c> or <c>--compare-to</c>:
    /// diffs only support Table and Json output, so any other requested format (and any
    /// <c>--output</c> for a non-Json diff) is rejected or falls back to Table, then the
    /// threshold/exit-code handling shared with a normal run applies.
    /// </summary>
    /// <param name="summary">The current-side summary of the diff.</param>
    /// <param name="flagName">
    /// The option name to name in the unsupported-format warning/error (<c>--baseline</c> or
    /// <c>--compare-to</c>).
    /// </param>
    /// <param name="options">The parsed options.</param>
    /// <param name="updateCheck">The in-flight update-check task, or <see langword="null"/> if it was not started.</param>
    /// <param name="version">The tool's own informational version, or <see langword="null"/> if unavailable.</param>
    /// <param name="renderJson">Renders the diff as Json to the given <see cref="TextWriter"/>.</param>
    /// <param name="renderTable">Renders the diff as a Table to the console.</param>
    private int RenderDiffAndFinish(
        AnalysisSummary summary,
        string flagName,
        AnalyzeOptions options,
        Task<UpdateCheckResult?>? updateCheck,
        string? version,
        Action<TextWriter> renderJson,
        Action renderTable)
    {
        if (options.Format is not (OutputFormat.Json or OutputFormat.Table))
        {
            if (options.OutputFile is not null && options.OutputFile != StdoutToken)
            {
                Console.Error.WriteLine(
                    $"sloc: {flagName} diff output is only supported for Table and Json formats; -f {options.Format.ToString().ToLowerInvariant()} with -o '{options.OutputFile}' cannot be honored.");
                return ExitCode.Error;
            }

            Console.Error.WriteLine(
                $"sloc: {flagName} diff output is only supported for Table and Json formats; ignoring -f {options.Format.ToString().ToLowerInvariant()} and rendering a table.");
        }

        if (options.Format == OutputFormat.Json && (options.OutputFile is null || options.OutputFile == StdoutToken))
        {
            renderJson(Console.Out);
        }
        else if (options.Format == OutputFormat.Json)
        {
            if (!WriteToFile(options.OutputFile!, renderJson, options.Quiet))
            {
                return ExitCode.Error;
            }
        }
        else
        {
            renderTable();
        }

        ReportUpdate(updateCheck, version);
        return ThresholdResult(options, summary);
    }

    /// <summary>
    /// Produces the <see cref="ScanResult"/> for a one-shot run: a git snapshot's temp files,
    /// an explicit <c>--list-file</c>, a progress-spinner-driven directory scan, or a plain
    /// directory scan, in that priority order. Scan-level failures (e.g. a missing directory)
    /// are left to propagate to <see cref="Execute"/>'s own catch.
    /// </summary>
    /// <param name="gitSnapshot">The extracted <c>--git-hash</c> snapshot, or <see langword="null"/> for a normal run.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="scanOptions">The scan options to apply.</param>
    /// <param name="showProgress">Whether a scanning spinner may be drawn.</param>
    private ScanResult RunScan(GitSnapshot? gitSnapshot, AnalyzeOptions options, ScanOptions scanOptions, bool showProgress)
    {
        if (gitSnapshot is not null)
        {
            return _scanner.ScanFiles(gitSnapshot.Files.Select(f => f.TempPath), scanOptions);
        }

        if (options.ListFile is { } listFile)
        {
            return _scanner.ScanFiles(ReadListFile(listFile), scanOptions);
        }

        if (!showProgress)
        {
            return _scanner.Scan(options.Path, scanOptions);
        }

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

        return result ?? throw new InvalidOperationException("Scan did not complete.");
    }

    /// <summary>
    /// Scans and analyzes once for <see cref="ExecuteWatch"/>: full re-scan (applying the
    /// same include/exclude/gitignore/gitattributes/language filters as a normal run),
    /// then <see cref="AnalyzeFiles"/> for the per-file analysis and aggregation into an
    /// <see cref="AnalysisSummary"/>.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="scanOptions">The scan options to apply for this pass.</param>
    private AnalysisSummary RunWatchPass(AnalyzeOptions options, ScanOptions scanOptions)
    {
        var scanResult = _scanner.Scan(options.Path, scanOptions);
        var skipped = new List<SkippedEntry>(scanResult.Skipped);
        var results = AnalyzeFiles(scanResult.Files, options, skipped);

        return new AnalysisSummary(
            results,
            skipped,
            options.Sort,
            descending: options.Sort != LanguageSort.Name,
            top: options.Top);
    }
}