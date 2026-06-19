using System.CommandLine;
using System.CommandLine.Help;
using Sloc.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pathArgument = new Argument<string>("path")
{
    Description = CliResources.CmdPathDescription,
    DefaultValueFactory = _ => "."
};

var includeOption = new Option<string[]>("--include", "-i")
{
    Description = CliResources.CmdIncludeDescription,
    AllowMultipleArgumentsPerToken = true
};

var excludeOption = new Option<string[]>("--exclude", "-e")
{
    Description = CliResources.CmdExcludeDescription,
    AllowMultipleArgumentsPerToken = true
};

var formatOption = new Option<OutputFormat?>("--format", "-f")
{
    Description = CliResources.CmdFormatDescription
};

var noRecursiveOption = new Option<bool>("--no-recursive")
{
    Description = CliResources.CmdNoRecursiveDescription
};

var noHealthOption = new Option<bool>("--no-health")
{
    Description = CliResources.CmdNoHealthDescription
};

var byFileOption = new Option<bool>("--by-file")
{
    Description = CliResources.CmdByFileDescription
};

var pagedOption = new Option<bool>("--paged", "-p")
{
    Description = CliResources.CmdPagedDescription
};

var allOption = new Option<bool>("--all")
{
    Description = CliResources.CmdAllDescription
};

var outputOption = new Option<string>("--output", "-o")
{
    Description = CliResources.CmdOutputDescription
};

var rootCommand = new RootCommand(CliResources.CmdRootDescription)
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
    noHealthOption
};

var helpOpt = rootCommand.Options.OfType<HelpOption>().FirstOrDefault();
helpOpt?.Description = CliResources.CmdHelpDescription;

var versionOpt = rootCommand.Options.OfType<VersionOption>().FirstOrDefault();
versionOpt?.Description = CliResources.CmdVersionDescription;

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
        NoHealth = parseResult.GetValue(noHealthOption)
    };

    return new AnalyzeHandler().Execute(options);
});

return rootCommand.Parse(args).Invoke();