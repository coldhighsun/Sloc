using System.CommandLine;
using System.CommandLine.Help;
using Sloc.Cli;
using Sloc.Core.Models;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pathArgument = new Argument<string>("path")
{
    Description = "The file or directory to analyze.",
    DefaultValueFactory = _ => "."
};

var includeOption = new Option<string[]>("--include", "-i")
{
    Description = "Glob pattern of files to include. Can be specified multiple times.",
    AllowMultipleArgumentsPerToken = true
};

var excludeOption = new Option<string[]>("--exclude", "-e")
{
    Description = "Glob pattern of files to exclude. Can be specified multiple times.",
    AllowMultipleArgumentsPerToken = true
};

var formatOption = new Option<OutputFormat?>("--format", "-f")
{
    Description = "Output format: Table, Json, or Html. Inferred from the --output extension when omitted."
};

var noRecursiveOption = new Option<bool>("--no-recursive")
{
    Description = "Do not descend into subdirectories."
};

var noHealthOption = new Option<bool>("--no-health")
{
    Description = "Hide the Comment Health column and percentage breakdowns."
};

var byFileOption = new Option<bool>("--by-file")
{
    Description = "Show a per-file breakdown in addition to the language summary."
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
    Description = "Output file path for Json and Html formats. Use '-' to write to stdout."
};

var quietOption = new Option<bool>("--quiet", "-q")
{
    Description = "Suppress the banner, progress UI, and 'Saved to' message."
};

var noProgressOption = new Option<bool>("--no-progress")
{
    Description = "Suppress the live table and progress bar."
};

var minCommentPctOption = new Option<double?>("--min-comment-pct")
{
    Description = "Fail (exit code 2) if the overall comment percentage is below this value."
};

var jobsOption = new Option<int?>("--jobs", "-j")
{
    Description = "Maximum number of files to analyze in parallel. Defaults to the processor count; use 1 for sequential."
};

var noGitignoreOption = new Option<bool>("--no-gitignore")
{
    Description = "Do not honor .gitignore files (they are respected by default)."
};

var baselineOption = new Option<string>("--baseline")
{
    Description = "Compare against a previously saved JSON report and show the line-count diff."
};

var sortOption = new Option<LanguageSort>("--sort")
{
    Description = "Order the language summary by: Total (default), Code, Comment, Blank, Files, or Name."
};

var topOption = new Option<int?>("--top")
{
    Description = "Show only the top N languages in the summary."
};

var noUpdateCheckOption = new Option<bool>("--no-update-check")
{
    Description = "Do not check GitHub for a newer release (checked by default, with a 2 second timeout)."
};

var rootCommand = new RootCommand("Sloc - counts code, comment, and blank lines in source files.")
{
    pathArgument,
    includeOption,
    excludeOption,
    formatOption,
    noRecursiveOption,
    byFileOption,
    pagedOption,
    allOption,
    outputOption,
    noHealthOption,
    quietOption,
    noProgressOption,
    minCommentPctOption,
    jobsOption,
    noGitignoreOption,
    baselineOption,
    sortOption,
    topOption,
    noUpdateCheckOption
};

var helpOpt = rootCommand.Options.OfType<HelpOption>().FirstOrDefault();
helpOpt?.Description = "Show help and usage information.";

var versionOpt = rootCommand.Options.OfType<VersionOption>().FirstOrDefault();
versionOpt?.Description = "Show version information.";

rootCommand.SetAction(parseResult =>
{
    var outputFile = parseResult.GetValue(outputOption);
    var explicitFormat = parseResult.GetValue(formatOption);

    var format = explicitFormat ?? outputFile switch
    {
        not null => Path.GetExtension(outputFile).ToLowerInvariant() switch
        {
            ".json" => OutputFormat.Json,
            ".html" or ".htm" => OutputFormat.Html,
            _ => OutputFormat.Table
        },
        _ => OutputFormat.Table
    };

    var options = new AnalyzeOptions
    {
        Path = parseResult.GetValue(pathArgument) ?? ".",
        Includes = parseResult.GetValue(includeOption) ?? [],
        Excludes = parseResult.GetValue(excludeOption) ?? [],
        Format = format,
        NoRecursive = parseResult.GetValue(noRecursiveOption),
        ByFile = parseResult.GetValue(byFileOption),
        Paged = parseResult.GetValue(pagedOption),
        IncludeUnknown = parseResult.GetValue(allOption),
        OutputFile = outputFile,
        NoHealth = parseResult.GetValue(noHealthOption),
        Quiet = parseResult.GetValue(quietOption),
        NoProgress = parseResult.GetValue(noProgressOption),
        MinCommentPct = parseResult.GetValue(minCommentPctOption),
        Jobs = parseResult.GetValue(jobsOption),
        RespectGitignore = !parseResult.GetValue(noGitignoreOption),
        BaselinePath = parseResult.GetValue(baselineOption),
        Sort = parseResult.GetValue(sortOption),
        Top = parseResult.GetValue(topOption),
        NoUpdateCheck = parseResult.GetValue(noUpdateCheckOption)
    };

    return new AnalyzeHandler().Execute(options);
});

try
{
    return rootCommand.Parse(args).Invoke();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"sloc: {ex.Message}");
    return ExitCode.Unexpected;
}