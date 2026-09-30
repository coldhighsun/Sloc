using Sloc.Cli.Analysis;
using Sloc.Cli.Parsing;
using System.Globalization;

// No BOM: matches the encoding AnalyzeHandler.WriteToFile uses for "-o <path>", so piped or
// redirected stdout output (the default, or "-o -") parses the same way file output does for
// a strict JSON/CSV consumer (e.g. jq).
try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    // No console is attached (e.g. a detached or service process); the encoding only matters
    // for a console, so keep the default rather than crashing before any work starts.
}

// Render all numbers with the invariant culture so report output (thousands separators,
// percentages) is deterministic regardless of the host's regional settings.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var rootCommand = SlocCommandFactory.Create(options => new AnalyzeHandler().Execute(options));

return CommandInvoker.Invoke(rootCommand.Parse(args.Length == 0 ? ["--help"] : args), Console.Error);
