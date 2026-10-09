using Sloc.Cli.Analysis;
using Sloc.Core.Languages;
using Sloc.Core.Models;
using System.CommandLine;
using System.CommandLine.Help;

namespace Sloc.Cli.Parsing;

/// <summary>
/// Builds the <c>sloc</c> command line: its arguments and options, the validation of their
/// combinations, and the mapping of a parsed command line to <see cref="AnalyzeOptions"/>.
/// Kept apart from <c>Program.cs</c> so parsing behavior can be tested without running an analysis.
/// </summary>
internal static class SlocCommandFactory
{
    /// <summary>
    /// Creates the root command. Its action validates the parsed arguments and passes the
    /// resulting <see cref="AnalyzeOptions"/> to <paramref name="execute"/>.
    /// </summary>
    /// <param name="execute">
    /// Runs the analysis for the validated options and returns the process exit code.
    /// </param>
    /// <returns>The configured root command.</returns>
    public static RootCommand Create(Func<AnalyzeOptions, int> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        var pathArgument = new Argument<string>("path")
        {
            Description = "The file or directory to analyze. Defaults to the current directory. Running sloc with no arguments at all shows this help instead.",
            DefaultValueFactory = _ => "."
        };

        var listFileOption = new Option<string>("--list-file")
        {
            Description = "Analyze exactly the files listed (one path per line) in this file instead of scanning a directory. Use '-' to read the list from stdin. Ignores 'path'."
        };

        var gitHashOption = new Option<string>("--git-hash")
        {
            Description = "Analyze the repository tree as of this commit/tree-ish, without checking it out. "
                + "'path' is used as the repo root to query. Requires git on PATH. Mutually exclusive with --list-file."
        };

        var compareToOption = new Option<string>("--compare-to")
        {
            Description = "Diff the current analysis against 'path' as of this other commit/tree-ish, without checking it out or saving a baseline file first. "
                + "Requires git on PATH. Combine with --git-hash to diff two commits directly. "
                + "Mutually exclusive with --baseline, --watch, and --list-file; output is Table or Json only, same as --baseline."
        };

        var includeOption = new Option<string[]>("--include", "-i")
        {
            Description = "Glob pattern of files to include. Can be specified multiple times.",
        };

        var excludeOption = new Option<string[]>("--exclude", "-e")
        {
            Description = "Glob pattern of files to exclude. Can be specified multiple times.",
        };

        var excludeDirOption = new Option<string[]>("--exclude-dir")
        {
            Description = "Directory name to exclude at any depth (e.g. \"vendor\"). "
                + "Shorthand for --exclude \"**/<name>/**\". Can be specified multiple times.",
        };

        var supportedLangNames = string.Join(", ", LanguageRegistry.Languages
            .Select(language => language.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        var includeLangOption = new Option<string[]>("--include-lang")
        {
            Description = $"Language name to include, matched case-insensitively. Can be specified multiple times. Supported: {supportedLangNames}.",
        };

        var excludeLangOption = new Option<string[]>("--exclude-lang")
        {
            Description = $"Language name to exclude, matched case-insensitively. Can be specified multiple times. Supported: {supportedLangNames}.",
        };

        var formatOption = new Option<OutputFormat?>("--format", "-f")
        {
            Description = "Output format: Table, Json, Html, Csv, or Markdown. Inferred from the --output extension when omitted."
        };

        var noRecursiveOption = new Option<bool>("--no-recursive")
        {
            Description = "Do not descend into subdirectories."
        };

        var noHealthOption = new Option<bool>("--no-health")
        {
            Description = "Hide the Comment Health column and percentage breakdowns."
        };

        var noComplexityOption = new Option<bool>("--no-complexity")
        {
            Description = "Hide the Complexity column/field."
        };

        var byFileOption = new Option<bool>("--by-file")
        {
            Description = "Show a per-file breakdown in addition to the language summary."
        };

        var detailedOption = new Option<bool>("--detailed")
        {
            Description = "In Json, Html, and Markdown output, include both the language summary and the per-file breakdown. In Csv output, selects the language summary only."
        };

        var pagedOption = new Option<bool>("--paged", "-p")
        {
            Description = "Enable pagination when displaying the per-file breakdown."
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Include files whose extension maps to no known language."
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output file path for Json, Html, Csv, and Markdown formats. Use '-' to write to stdout."
        };

        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Suppress the banner, progress UI, and 'Saved to' message."
        };

        var noProgressOption = new Option<bool>("--no-progress")
        {
            Description = "Suppress the live table and progress bar."
        };

        var watchOption = new Option<bool>("--watch", "-w")
        {
            Description = "Watch the path for file changes and re-run the analysis, refreshing the table. Table format only; press Ctrl+C to stop."
        };

        var minCommentPctOption = new Option<double?>("--min-comment-pct")
        {
            Description = "Fail (exit code 2) if the overall comment percentage is below this value."
        };

        var jobsOption = new Option<int?>("--jobs", "-j")
        {
            Description = "Maximum number of files to analyze in parallel. Defaults to the processor count (also used for 0 or less); use 1 for sequential."
        };

        var noGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Do not honor .gitignore files (they are respected by default), and scan "
                + "nested repositories such as submodules, which are otherwise skipped inside a repository "
                + "(--compare-to always skips them)."
        };

        var noGitAttributesOption = new Option<bool>("--no-gitattributes")
        {
            Description = "Do not exclude files marked linguist-vendored or linguist-generated in "
                + ".gitattributes (excluded by default)."
        };

        var followSymlinksOption = new Option<bool>("--follow-symlinks")
        {
            Description = "Include symlinked/junctioned directories and symlinked files instead of skipping them. "
                + "A directory symlink that loops directly back to one of its own ancestors is still skipped."
        };

        var baselineOption = new Option<string>("--baseline")
        {
            Description = "Compare against a previously saved JSON report and show the line-count diff."
        };

        var sortOption = new Option<LanguageSort>("--sort")
        {
            Description = "Order the language summary by: Total (default), Code, Comment, Blank, Files, Name, or CommentPct."
        };

        var topOption = new Option<int?>("--top")
        {
            Description = "Show only the top N languages in the summary. The totals still cover every language; Table, Markdown, and Html output say so, Csv does not."
        };

        var noUpdateCheckOption = new Option<bool>("--no-update-check")
        {
            Description = "Do not check GitHub for a newer release (checked by default, with a 2 second timeout)."
        };

        var uniqueOption = new Option<bool>("--unique")
        {
            Description = "Count each distinct file content only once; later byte-identical duplicates are reported as skipped."
        };

        var rootCommand = new RootCommand("Sloc - counts code, comment, and blank lines in source files.")
        {
            pathArgument,
            listFileOption,
            gitHashOption,
            compareToOption,
            includeOption,
            excludeOption,
            excludeDirOption,
            includeLangOption,
            excludeLangOption,
            formatOption,
            noRecursiveOption,
            byFileOption,
            detailedOption,
            pagedOption,
            allOption,
            outputOption,
            noHealthOption,
            noComplexityOption,
            quietOption,
            noProgressOption,
            watchOption,
            minCommentPctOption,
            jobsOption,
            noGitignoreOption,
            noGitAttributesOption,
            followSymlinksOption,
            baselineOption,
            sortOption,
            topOption,
            noUpdateCheckOption,
            uniqueOption
        };

        var helpOpt = rootCommand.Options.OfType<HelpOption>().FirstOrDefault();
        helpOpt?.Description = "Show help and usage information.";

        var versionOpt = rootCommand.Options.OfType<VersionOption>().FirstOrDefault();
        versionOpt?.Description = "Show version information.";

        rootCommand.SetAction(parseResult =>
        {
            var outputFile = parseResult.GetValue(outputOption);
            var explicitFormat = parseResult.GetValue(formatOption);
            if (!CliArgumentValidation.IsValidFormat(explicitFormat))
            {
                Console.Error.WriteLine("sloc: --format must be Table, Json, Html, Csv, or Markdown.");
                return ExitCode.Error;
            }

            var format = FormatResolver.Resolve(explicitFormat, outputFile);

            // Table output never writes a file, so an --output path would be silently discarded.
            // Warn rather than fail so scripts relying on the exit code are unaffected.
            if (FormatResolver.OutputIgnoredForTable(format, outputFile))
            {
                Console.Error.WriteLine(
                    "sloc: --output is ignored for Table format; pass -f json|html|csv|markdown or use a recognized file extension.");
            }

            var minCommentPct = parseResult.GetValue(minCommentPctOption);
            if (!CliArgumentValidation.IsValidMinCommentPct(minCommentPct))
            {
                Console.Error.WriteLine("sloc: --min-comment-pct must be between 0 and 100.");
                return ExitCode.Error;
            }

            var top = parseResult.GetValue(topOption);
            if (!CliArgumentValidation.IsValidTop(top))
            {
                Console.Error.WriteLine("sloc: --top must be 1 or greater.");
                return ExitCode.Error;
            }

            if (!Enum.IsDefined(parseResult.GetValue(sortOption)))
            {
                Console.Error.WriteLine("sloc: --sort must be Total, Code, Comment, Blank, Files, Name, or CommentPct.");
                return ExitCode.Error;
            }

            var includeLangs = parseResult.GetValue(includeLangOption) ?? [];
            var excludeLangs = parseResult.GetValue(excludeLangOption) ?? [];
            if (CliArgumentValidation.FindUnknownLanguage([.. includeLangs, .. excludeLangs]) is { } unknownLanguage)
            {
                Console.Error.WriteLine(
                    $"sloc: unknown language '{unknownLanguage}' for --include-lang/--exclude-lang. See --help for the supported names.");
                return ExitCode.Error;
            }

            var listFile = parseResult.GetValue(listFileOption);
            var gitHash = parseResult.GetValue(gitHashOption);
            if (listFile is not null && gitHash is not null)
            {
                Console.Error.WriteLine("sloc: --git-hash and --list-file cannot be used together.");
                return ExitCode.Error;
            }

            var watch = parseResult.GetValue(watchOption);
            var baselinePath = parseResult.GetValue(baselineOption);
            var watchError = CliArgumentValidation.ValidateWatch(watch, listFile, gitHash, format, baselinePath);
            if (watchError is not null)
            {
                Console.Error.WriteLine(watchError);
                return ExitCode.Error;
            }

            var compareTo = parseResult.GetValue(compareToOption);
            var compareToError = CliArgumentValidation.ValidateCompareTo(compareTo, baselinePath, watch, listFile);
            if (compareToError is not null)
            {
                Console.Error.WriteLine(compareToError);
                return ExitCode.Error;
            }

            var options = new AnalyzeOptions
            {
                Path = parseResult.GetValue(pathArgument) ?? ".",
                ListFile = listFile,
                GitHash = gitHash,
                CompareTo = compareTo,
                Includes = parseResult.GetValue(includeOption) ?? [],
                Excludes = [
                    .. parseResult.GetValue(excludeOption) ?? [],
                    .. (parseResult.GetValue(excludeDirOption) ?? []).Select(name => $"**/{name}/**")
                ],
                IncludeLangs = includeLangs,
                ExcludeLangs = excludeLangs,
                Format = format,
                NoRecursive = parseResult.GetValue(noRecursiveOption),
                ByFile = parseResult.GetValue(byFileOption),
                Detailed = parseResult.GetValue(detailedOption),
                Paged = parseResult.GetValue(pagedOption),
                IncludeUnknown = parseResult.GetValue(allOption),
                OutputFile = outputFile,
                NoHealth = parseResult.GetValue(noHealthOption),
                NoComplexity = parseResult.GetValue(noComplexityOption),
                Quiet = parseResult.GetValue(quietOption),
                NoProgress = parseResult.GetValue(noProgressOption),
                MinCommentPct = minCommentPct,
                Jobs = parseResult.GetValue(jobsOption),
                RespectGitignore = !parseResult.GetValue(noGitignoreOption),
                RespectGitAttributes = !parseResult.GetValue(noGitAttributesOption),
                FollowSymlinks = parseResult.GetValue(followSymlinksOption),
                BaselinePath = baselinePath,
                Sort = parseResult.GetValue(sortOption),
                Top = top,
                NoUpdateCheck = parseResult.GetValue(noUpdateCheckOption),
                Unique = parseResult.GetValue(uniqueOption),
                Watch = watch
            };

            return execute(options);
        });

        return rootCommand;
    }
}
