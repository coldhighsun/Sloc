namespace Sloc.Cli.Analysis;

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