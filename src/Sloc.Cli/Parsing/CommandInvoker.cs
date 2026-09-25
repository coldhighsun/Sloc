using Sloc.Cli.Analysis;
using System.CommandLine;

namespace Sloc.Cli.Parsing;

/// <summary>
/// Invokes a parsed command line, mapping an exception that escapes the command's action to
/// <see cref="ExitCode.Unexpected"/>.
/// </summary>
internal static class CommandInvoker
{
    /// <summary>
    /// Invokes <paramref name="parseResult"/> with <c>System.CommandLine</c>'s default exception
    /// handler disabled. That handler would otherwise catch every exception and return
    /// <c>1</c>, the same code as <see cref="ExitCode.Error"/>, so scripts couldn't tell a
    /// crash from a bad path.
    /// </summary>
    /// <param name="parseResult">The parsed command line to invoke.</param>
    /// <param name="error">The writer for parse errors and the unexpected-error report.</param>
    /// <returns>
    /// The action's exit code, or <see cref="ExitCode.Unexpected"/> if it threw.
    /// </returns>
    internal static int Invoke(ParseResult parseResult, TextWriter error)
    {
        try
        {
            return parseResult.Invoke(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                Error = error
            });
        }
        catch (Exception ex)
        {
            // Last-resort handler: report the full exception so it can be filed as a bug.
            error.WriteLine($"sloc: unexpected error: {ex}");
            return ExitCode.Unexpected;
        }
    }
}
