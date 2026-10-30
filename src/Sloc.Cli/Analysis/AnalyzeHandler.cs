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
    /// Guards <see cref="_interruptCleanups"/>, which the Ctrl+C handler reads from another thread.
    /// </summary>
    private readonly Lock _cleanupLock = new();

    /// <summary>
    /// Deleters for the git snapshot temp directories that still exist (or are being
    /// extracted), so an interrupt (which skips <c>finally</c>/<c>using</c>) can remove them
    /// before the process exits.
    /// </summary>
    private readonly List<Action> _interruptCleanups = [];

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

        // A one-shot run has no cancellation of its own: Ctrl+C terminates the process without
        // running finally/using blocks, which would leave extracted snapshots in the temp directory.
        Console.CancelKeyPress += OnInterrupt;
        try
        {
            return ExecuteOnce(options, ctx);
        }
        finally
        {
            Console.CancelKeyPress -= OnInterrupt;
        }
    }

    /// <summary>
    /// Deletes any git snapshot temp directories still on disk when the user interrupts the
    /// run. Does not cancel the event, so the process still terminates as usual.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The event data.</param>
    private void OnInterrupt(object? sender, ConsoleCancelEventArgs e)
    {
        Action[] cleanups;
        lock (_cleanupLock)
        {
            cleanups = [.. _interruptCleanups];
        }

        foreach (var cleanup in cleanups)
        {
            cleanup();
        }
    }

    /// <summary>
    /// Registers <paramref name="cleanup"/> to run if the user interrupts the process.
    /// </summary>
    /// <param name="cleanup">The temp-directory deleter to run on interrupt.</param>
    private void TrackCleanup(Action cleanup)
    {
        lock (_cleanupLock)
        {
            _interruptCleanups.Add(cleanup);
        }
    }

    /// <summary>
    /// Stops tracking <paramref name="cleanup"/> once its directory has been handled normally.
    /// </summary>
    /// <param name="cleanup">The deleter previously passed to <see cref="TrackCleanup"/>.</param>
    private void UntrackCleanup(Action cleanup)
    {
        lock (_cleanupLock)
        {
            _interruptCleanups.Remove(cleanup);
        }
    }

    /// <summary>
    /// Extracts <paramref name="gitRef"/> with an interrupt cleanup registered for the whole
    /// extraction, so Ctrl+C mid-extraction also removes the partly written temp directory.
    /// The finished snapshot's own cleanup is registered before the extractor's is removed, so
    /// the directory is never uncovered in between. The caller must pass the returned cleanup
    /// to <see cref="UntrackCleanup"/> once it has disposed the snapshot.
    /// </summary>
    /// <param name="repoPath">The path inside the repository to query.</param>
    /// <param name="gitRef">The commit/tree-ish to extract.</param>
    /// <returns>The snapshot and the interrupt cleanup registered for it.</returns>
    private (GitSnapshot Snapshot, Action Cleanup) ExtractTracked(string repoPath, string gitRef)
    {
        var extractor = new GitSnapshotExtractor();
        Action extractorCleanup = extractor.DeletePendingTempRoot;
        TrackCleanup(extractorCleanup);
        try
        {
            var snapshot = extractor.Extract(repoPath, gitRef);
            Action snapshotCleanup = snapshot.Dispose;
            TrackCleanup(snapshotCleanup);
            return (snapshot, snapshotCleanup);
        }
        finally
        {
            UntrackCleanup(extractorCleanup);
        }
    }

    /// <summary>
    /// Runs a single (non-watch) analysis using the already-built <paramref name="ctx"/>.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="ctx">The run context built by <see cref="BuildRunContext"/>.</param>
    private int ExecuteOnce(AnalyzeOptions options, RunContext ctx)
    {
        GitSnapshot? gitSnapshot = null;
        Action? snapshotCleanup = null;
        if (options.GitHash is { } gitHash)
        {
            try
            {
                (gitSnapshot, snapshotCleanup) = ExtractTracked(options.Path, gitHash);
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
            if (gitSnapshot is not null && snapshotCleanup is not null)
            {
                gitSnapshot.Dispose();
                UntrackCleanup(snapshotCleanup);
            }
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
    /// <param name="cancellationToken">
    /// Cancels the analysis (e.g. on Ctrl+C during the first <c>--watch</c> pass), surfacing
    /// as an <see cref="OperationCanceledException"/>.
    /// </param>
    internal List<FileAnalysis> AnalyzeFiles(
        IReadOnlyList<ScannedFile> files,
        AnalyzeOptions options,
        List<SkippedEntry> skipped,
        Action<int, FileAnalysis?>? onFileAnalyzed = null,
        CancellationToken cancellationToken = default)
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
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellationToken }, AnalyzeAt);

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
        var (snapshot, cleanup) = ExtractTracked(repoPath, gitRef);
        try
        {
            return AnalyzeSnapshot(snapshot, repoPath, scanOptions, options);
        }
        finally
        {
            snapshot.Dispose();
            UntrackCleanup(cleanup);
        }
    }

    /// <summary>
    /// Scans and analyzes an extracted <paramref name="snapshot"/> for
    /// <see cref="AnalyzeGitRef"/>.
    /// </summary>
    /// <param name="snapshot">The extracted snapshot of the compared ref.</param>
    /// <param name="repoPath">The path passed as <c>path</c>.</param>
    /// <param name="scanOptions">The scan options to apply to the extracted snapshot.</param>
    /// <param name="options">The parsed options.</param>
    private AnalysisSummary AnalyzeSnapshot(GitSnapshot snapshot, string repoPath, ScanOptions scanOptions, AnalyzeOptions options)
    {
        var isFilePath = File.Exists(repoPath);
        var filesInScope = snapshot.Files;
        IEnumerable<SkippedEntry> skippedInScope = snapshot.Skipped;
        if (options.GitHash is null && snapshot.RelativePrefix.Length > 0)
        {
            // A file path scopes to exactly that entry and a directory path to entries under it,
            // even if the ref had the other kind at that path (a file now a directory, or vice
            // versa): that entry is not part of what the current side analyzes.
            var relativePrefix = snapshot.RelativePrefix;
            bool InScope(string gitPath) => isFilePath
                ? gitPath.Equals(relativePrefix, StringComparison.Ordinal)
                : gitPath.StartsWith(relativePrefix + "/", StringComparison.Ordinal);

            filesInScope = snapshot.Files.Where(f => InScope(f.GitPath)).ToList();
            skippedInScope = snapshot.Skipped.Where(s => InScope(s.Path));
        }

        // The commit's whole tree, whose rule files above the scanned path replace the working tree's.
        var repository = new SnapshotRepository(
            snapshot.RelativePrefix, [.. snapshot.Files.Select(f => new SnapshotEntry(f.TempPath, f.GitPath))]);
        ScanResult scanResult;
        if (options.GitHash is not null)
        {
            // Both sides are git snapshots analyzed the same way (no directory filtering).
            scanResult = _scanner.ScanFiles(filesInScope.Select(f => f.TempPath), scanOptions);
        }
        else if (isFilePath)
        {
            // A single file: filtered by the same single-file rules the current side's Scan
            // applied, with the rule files taken from the commit.
            scanResult = filesInScope.Count == 0
                ? new ScanResult([], [])
                : _scanner.ScanSnapshotFile(repoPath, filesInScope[0].TempPath, scanOptions, repository);
        }
        else
        {
            // The current side is a filtered directory scan, so the baseline must be filtered
            // the same way (globs, built-in excludes, .gitignore/.gitattributes, recursion),
            // or anything those filters exclude would show up as a spurious delta. The rule
            // files above the subdirectory come from the commit too, not the working tree.
            var prefixLength = snapshot.RelativePrefix.Length == 0 ? 0 : snapshot.RelativePrefix.Length + 1;
            scanResult = _scanner.ScanSnapshot(
                repoPath,
                [.. filesInScope.Select(f => new SnapshotEntry(f.TempPath, f.GitPath[prefixLength..]))],
                scanOptions,
                repository);
        }

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

        AnalysisSummary? summary = null;

        AnsiConsole.Live(tableRenderer.BuildLanguageTable(new AnalysisSummary([]), noHealth: options.NoHealth, noComplexity: options.NoComplexity))
            .AutoClear(false)
            .Start(ctx =>
            {
                var results = AnalyzeIntoLiveTable(ctx.UpdateTarget, files, options, skipped, tableRenderer);

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
    /// Analyzes <paramref name="files"/> on a background task while this thread keeps
    /// refreshing the live table through <paramref name="showFrame"/> from a thread-safe
    /// <see cref="LiveAggregator"/> (no per-tick re-aggregation). Returns once the analysis
    /// has finished; the caller draws the final frame.
    /// </summary>
    /// <param name="showFrame">Displays one frame of the live table (e.g. <c>LiveDisplayContext.UpdateTarget</c>).</param>
    /// <param name="files">The files to analyze.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="skipped">The skipped-entries list per-file failures are appended to.</param>
    /// <param name="tableRenderer">The table renderer used to build each frame.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    internal List<FileAnalysis> AnalyzeIntoLiveTable(
        Action<Table> showFrame,
        IReadOnlyList<ScannedFile> files,
        AnalyzeOptions options,
        List<SkippedEntry> skipped,
        TableRenderer tableRenderer,
        CancellationToken cancellationToken = default)
    {
        var aggregator = new LiveAggregator(options.Sort, options.Top, unique: options.Unique);
        var work = Task.Run(() => AnalyzeFiles(files, options, skipped, (_, analysis) =>
        {
            if (analysis is not null)
            {
                aggregator.Add(analysis);
            }
        }, cancellationToken));

        while (!work.IsCompleted)
        {
            showFrame(tableRenderer.BuildLanguageTable(
                aggregator.ToSummary(),
                $"[grey]Analyzing... {aggregator.FilesProcessed:N0} / {files.Count:N0}[/]",
                noHealth: options.NoHealth,
                noComplexity: options.NoComplexity));
            Thread.Sleep(LiveTableRefreshInterval);
        }

        return work.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Analyzes the already-scanned files of the first <see cref="ExecuteWatch"/> pass behind the
    /// live table and draws the finished table as the last frame.
    /// </summary>
    /// <param name="showFrame">Displays one frame of the live table.</param>
    /// <param name="scanned">The scan result of the first pass.</param>
    /// <param name="options">The parsed options.</param>
    /// <param name="tableRenderer">The table renderer used to build each frame.</param>
    /// <param name="cancellationToken">Cancels the analysis (Ctrl+C).</param>
    /// <returns>
    /// The finished summary, or <see langword="null"/> if the pass was cancelled, in which case the
    /// half-filled frame is replaced by an empty table so partial totals do not linger as a result.
    /// </returns>
    internal AnalysisSummary? AnalyzeFirstWatchPass(
        Action<Table> showFrame,
        ScanResult scanned,
        AnalyzeOptions options,
        TableRenderer tableRenderer,
        CancellationToken cancellationToken)
    {
        var skipped = new List<SkippedEntry>(scanned.Skipped);
        try
        {
            var summary = BuildWatchSummary(
                AnalyzeIntoLiveTable(showFrame, scanned.Files, options, skipped, tableRenderer, cancellationToken),
                skipped,
                options);
            showFrame(tableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth, noComplexity: options.NoComplexity));
            return summary;
        }
        catch (OperationCanceledException)
        {
            showFrame(tableRenderer.BuildLanguageTable(
                new AnalysisSummary([]),
                "[grey]Cancelled before the first pass finished.[/]",
                noHealth: options.NoHealth,
                noComplexity: options.NoComplexity));
            return null;
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

        // The live table is redrawn in place with cursor movement, which Spectre.Console cannot
        // do (and may throw on) when stdout is piped or redirected.
        if (Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("sloc: --watch requires an interactive terminal; stdout is redirected.");
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

        // Signaled immediately on Ctrl+C so the debounce wait below wakes up right away,
        // instead of a plain Thread.Sleep leaving Ctrl+C waiting out the rest of the interval.
        // Also the cancellation flag itself: its state is safe to read across threads. Deliberately
        // not disposed: a late Ctrl+C can still run the handler on another thread while this method
        // unwinds, and an event whose WaitHandle was never requested holds no unmanaged resources.
        var cancelSignal = new ManualResetEventSlim(false);
        // Also cancels a first pass that is still scanning/analyzing, which cancelSignal alone
        // would only be noticed after. Not disposed, for the same reason as cancelSignal.
        var cancellation = new CancellationTokenSource();
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

            try
            {
                watcher = StartWatcher();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }

            // With progress UI the first pass scans behind a spinner and is analyzed behind the
            // live table below; without it (quiet/--no-progress) it is analyzed silently here.
            ScanResult? scanned = null;
            AnalysisSummary? silentSummary = null;
            try
            {
                if (!options.Quiet && !options.NoProgress)
                {
                    scanned = ScanWithStatus(options, scanOptions, cancellation.Token);
                }
                else
                {
                    silentSummary = RunWatchPass(options, scanOptions, cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (!options.Quiet)
                {
                    AnsiConsole.MarkupLine("[grey]Cancelled before the first pass finished.[/]");
                }

                return WatchExitCode(options, lastSummary: null);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitCode.Error;
            }

            string? firstPassError = null;
            AnsiConsole.Live(tableRenderer.BuildLanguageTable(silentSummary ?? new AnalysisSummary([]), noHealth: options.NoHealth, noComplexity: options.NoComplexity))
                .AutoClear(false)
                .Start(ctx =>
                {
                    if (scanned is { } firstScan)
                    {
                        try
                        {
                            lastSummary = AnalyzeFirstWatchPass(ctx.UpdateTarget, firstScan, options, tableRenderer, cancellation.Token);
                        }
                        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                        {
                            // The first pass analyzed behind the live table failed the same way the scan above can.
                            firstPassError = ex.Message;
                            return;
                        }

                    }
                    else
                    {
                        lastSummary = silentSummary;
                    }

                    // Null only when Ctrl+C cancelled the first pass.
                    if (lastSummary is not { } shown)
                    {
                        ctx.Refresh();
                        return;
                    }

                    ctx.Refresh();

                    var retry = new WatchRetryState();
                    var clock = Stopwatch.StartNew();
                    while (!cancelSignal.IsSet)
                    {
                        cancelSignal.Wait(WatchDebounceInterval);

                        if (cancelSignal.IsSet)
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

                        var changePending = Interlocked.Exchange(ref pendingChange, 0) != 0;
                        if (!retry.ShouldRescan(changePending, clock.Elapsed))
                        {
                            continue;
                        }

                        retry.BeginRescan();
                        string caption;
                        try
                        {
                            shown = lastSummary = RunWatchPass(options, scanOptions, cancellation.Token);
                            lastPassFailed = false;
                            retry.Reset();
                            caption = $"[grey]Last update: {DateTimeOffset.Now:T}[/]";
                        }
                        catch (OperationCanceledException)
                        {
                            // Ctrl+C during a rescan: keep the last good table and stop watching.
                            break;
                        }
                        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
                        {
                            // E.g. the watched directory was deleted or became unreadable:
                            // keep the last good table on screen instead of ending the watch.
                            lastPassFailed = true;
                            var watchDirExists = Directory.Exists(watchDir);
                            // A vanished directory is polled for, not retried on a timer; any other
                            // failure is retried since no change event will announce it has cleared.
                            retry.RecordFailedRescan(watchDirExists, clock.Elapsed);
                            if (!watchDirExists)
                            {
                                watcher?.Dispose();
                                watcher = null;
                                caption = $"[yellow]{Markup.Escape(watchDir)} was removed at {DateTimeOffset.Now:T}; waiting for it to reappear.[/]";
                            }
                            else
                            {
                                caption = $"[yellow]Rescan failed at {DateTimeOffset.Now:T}: {Markup.Escape(ex.Message)}[/]";
                            }
                        }

                        ctx.UpdateTarget(tableRenderer.BuildLanguageTable(
                            shown,
                            caption,
                            noHealth: options.NoHealth,
                            noComplexity: options.NoComplexity));
                    }
                });

            if (firstPassError is not null)
            {
                Console.Error.WriteLine(firstPassError);
                return ExitCode.Error;
            }
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
            cancelSignal.Set();
            cancellation.Cancel();
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
            IEnumerable<string> entries;
            try
            {
                entries = ReadListFile(listFile);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // An empty or malformed path is a user input error, reported like any other
                // unreadable list file rather than as an unexpected failure.
                throw new IOException($"Invalid --list-file path '{listFile}': {ex.Message}", ex);
            }

            return _scanner.ScanFiles(entries, scanOptions);
        }

        return showProgress
            ? ScanWithStatus(options, scanOptions)
            : _scanner.Scan(options.Path, scanOptions);
    }

    /// <summary>
    /// Scans <see cref="AnalyzeOptions.Path"/> behind a status spinner that reports the files
    /// found (or directories checked for ignore rules) so far. Shared by the one-shot run and
    /// the first pass of <see cref="ExecuteWatch"/>.
    /// </summary>
    /// <param name="options">The parsed options.</param>
    /// <param name="scanOptions">The scan options to apply.</param>
    /// <param name="cancellationToken">Aborts the scan with an <see cref="OperationCanceledException"/>.</param>
    private ScanResult ScanWithStatus(AnalyzeOptions options, ScanOptions scanOptions, CancellationToken cancellationToken = default)
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
                    cancellationToken: cancellationToken,
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
    /// <param name="cancellationToken">
    /// Aborts the scan or analysis with an <see cref="OperationCanceledException"/>.
    /// </param>
    internal AnalysisSummary RunWatchPass(
        AnalyzeOptions options,
        ScanOptions scanOptions,
        CancellationToken cancellationToken = default)
    {
        var scanResult = _scanner.Scan(options.Path, scanOptions, cancellationToken: cancellationToken);
        var skipped = new List<SkippedEntry>(scanResult.Skipped);
        var results = AnalyzeFiles(scanResult.Files, options, skipped, cancellationToken: cancellationToken);

        return BuildWatchSummary(results, skipped, options);
    }

    /// <summary>
    /// Aggregates one <see cref="ExecuteWatch"/> pass's results into the summary its live
    /// table shows, shared by the first pass and every rescan.
    /// </summary>
    /// <param name="results">The per-file analyses.</param>
    /// <param name="skipped">The skipped entries of the pass.</param>
    /// <param name="options">The parsed options.</param>
    internal static AnalysisSummary BuildWatchSummary(
        IReadOnlyList<FileAnalysis> results,
        IReadOnlyList<SkippedEntry> skipped,
        AnalyzeOptions options) =>
        new(
            results,
            skipped,
            options.Sort,
            descending: options.Sort != LanguageSort.Name,
            top: options.Top);
}