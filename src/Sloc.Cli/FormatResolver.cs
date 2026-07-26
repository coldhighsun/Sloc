namespace Sloc.Cli;

/// <summary>
/// Resolves the effective <see cref="OutputFormat"/> from an explicit <c>--format</c>
/// value and/or the <c>--output</c> file extension.
/// </summary>
internal static class FormatResolver
{
    /// <summary>
    /// Determines the output format to use. An explicit format always wins; otherwise the
    /// format is inferred from the output file's extension, falling back to
    /// <see cref="OutputFormat.Table"/>.
    /// </summary>
    /// <param name="explicitFormat">
    /// The value of <c>--format</c>, or <see langword="null"/> when it was not specified.
    /// </param>
    /// <param name="outputFile">
    /// The value of <c>--output</c>, or <see langword="null"/> when it was not specified.
    /// </param>
    /// <returns>
    /// The resolved output format.
    /// </returns>
    public static OutputFormat Resolve(OutputFormat? explicitFormat, string? outputFile)
    {
        if (explicitFormat is { } format)
        {
            return format;
        }

        if (outputFile is null)
        {
            return OutputFormat.Table;
        }

        return Path.GetExtension(outputFile).ToLowerInvariant() switch
        {
            ".json" => OutputFormat.Json,
            ".html" or ".htm" => OutputFormat.Html,
            ".csv" => OutputFormat.Csv,
            ".md" or ".markdown" => OutputFormat.Markdown,
            _ => OutputFormat.Table
        };
    }

    /// <summary>
    /// Gets a value indicating whether an <c>--output</c> file path would be silently
    /// ignored: a real file path was supplied (not <see langword="null"/> and not the
    /// stdout token <c>-</c>) but the resolved format is <see cref="OutputFormat.Table"/>,
    /// which never writes a file.
    /// </summary>
    /// <param name="format">The resolved output format.</param>
    /// <param name="outputFile">The value of <c>--output</c>.</param>
    /// <returns>
    /// <see langword="true"/> when the output path will be ignored; otherwise <see langword="false"/>.
    /// </returns>
    public static bool OutputIgnoredForTable(OutputFormat format, string? outputFile) =>
        format == OutputFormat.Table
        && !string.IsNullOrEmpty(outputFile)
        && outputFile != "-";
}
