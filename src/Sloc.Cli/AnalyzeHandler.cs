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
/// The parsed options for an analysis run.
/// </summary>
public sealed class AnalyzeOptions
{
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
    /// When <see langword="true"/>, the Comment Health column and percentage
    /// breakdowns are hidden.
    /// </summary>
    public bool NoHealth
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
}

/// <summary>
/// Orchestrates an analysis run: scan files, analyze each one, aggregate the
/// results, and render them.
/// </summary>
public sealed class AnalyzeHandler
{
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

        if (options.Format == OutputFormat.Table && !Console.IsOutputRedirected)
        {
            var version = typeof(AnalyzeHandler).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrEmpty(version))
            {
                AnsiConsole.MarkupLine($"[grey]sloc {Markup.Escape(version)}[/]");
            }
        }

        ScanResult scanResult;
        try
        {
            scanResult = _scanner.Scan(options.Path, new ScanOptions
            {
                Includes = options.Includes,
                Excludes = options.Excludes,
                Recursive = !options.NoRecursive,
                IncludeUnknown = options.IncludeUnknown
            });
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var files = scanResult.Files;
        var results = new List<FileAnalysis>(files.Count);
        var skipped = new List<SkippedEntry>(scanResult.Skipped);

        void AnalyzeFile(ScannedFile file)
        {
            try
            {
                results.Add(_analyzer.Analyze(file.Path, file.Language));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedEntry(file.Path, ex.Message));
            }
        }

        if (Console.IsOutputRedirected)
        {
            // Spectre's live table / progress bar drive the console cursor, which throws
            // when stdout is redirected (pipes, CI, files). Analyze without any live UI.
            foreach (var file in files)
            {
                AnalyzeFile(file);
            }
        }
        else if (options is { Format: OutputFormat.Table, ByFile: false } && files.Count > 0)
        {
            AnsiConsole.Live(TableRenderer.BuildLanguageTable(new AnalysisSummary(results), noHealth: options.NoHealth))
                .AutoClear(false)
                .Start(ctx =>
                {
                    var sw = Stopwatch.StartNew();
                    foreach (var file in files)
                    {
                        AnalyzeFile(file);
                        if (sw.ElapsedMilliseconds >= 100)
                        {
                            ctx.UpdateTarget(TableRenderer.BuildLanguageTable(
                                new AnalysisSummary(results),
                                $"[grey]Analyzing... {results.Count:N0} / {files.Count:N0}[/]",
                                noHealth: options.NoHealth));
                            sw.Restart();
                        }
                    }

                    ctx.UpdateTarget(TableRenderer.BuildLanguageTable(new AnalysisSummary(results), noHealth: options.NoHealth));
                });
        }
        else if (files.Count > 0)
        {
            AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var task = ctx.AddTask("[green]Analyzing[/]", maxValue: files.Count);
                    foreach (var file in files)
                    {
                        AnalyzeFile(file);
                        task.Increment(1);
                    }
                });
        }

        var summary = new AnalysisSummary(results, skipped);
        if (options.Format == OutputFormat.Json)
        {
            var outputFile = options.OutputFile ?? "sloc-report.json";
            using var writer = new StreamWriter(outputFile, append: false, System.Text.Encoding.UTF8);
            new JsonRenderer(writer).Render(summary, options.ByFile, options.NoHealth);
            AnsiConsole.MarkupLine($"[green]Saved to:[/] {Markup.Escape(outputFile)}");
        }
        else if (options.Format == OutputFormat.Html)
        {
            var outputFile = options.OutputFile ?? "sloc-report.html";
            using var writer = new StreamWriter(outputFile, append: false, System.Text.Encoding.UTF8);
            new HtmlRenderer(writer).Render(summary, options.ByFile, options.NoHealth);
            AnsiConsole.MarkupLine($"[green]Saved to:[/] {Markup.Escape(outputFile)}");
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
            else if (Console.IsOutputRedirected)
            {
                // The live table is skipped when redirected, so render the final table here.
                AnsiConsole.Write(TableRenderer.BuildLanguageTable(summary, noHealth: options.NoHealth));
            }

            TableRenderer.RenderSkipped(summary);
        }
        return 0;
    }
}