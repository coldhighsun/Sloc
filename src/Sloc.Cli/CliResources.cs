using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Sloc.Cli;

/// <summary>
/// Provides strongly-typed, culture-aware access to localized string resources.
/// The current <see cref="CultureInfo.CurrentUICulture"/> is used at each access,
/// so setting it before the first call is sufficient to switch language.
/// </summary>
internal static class CliResources
{
    private static readonly ResourceManager Rm =
        new("Sloc.Cli.CliResources", typeof(CliResources).Assembly);

    /// <summary>
    /// Gets the --all option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdAllDescription => Get();

    /// <summary>
    /// Gets the --by-file option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdByFileDescription => Get();

    /// <summary>
    /// Gets the --exclude option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdExcludeDescription => Get();

    /// <summary>
    /// Gets the --format option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdFormatDescription => Get();

    /// <summary>
    /// Gets the --help option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdHelpDescription => Get();

    /// <summary>
    /// Gets the --include option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdIncludeDescription => Get();

    /// <summary>
    /// Gets the --no-recursive option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdNoRecursiveDescription => Get();

    /// <summary>
    /// Gets the --no-health option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdNoHealthDescription => Get();

    /// <summary>
    /// Gets the --output option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdOutputDescription => Get();

    /// <summary>
    /// Gets the --paged option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdPagedDescription => Get();

    /// <summary>
    /// Gets the path argument description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdPathDescription => Get();

    /// <summary>
    /// Gets the root command description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdRootDescription => Get();

    /// <summary>
    /// Gets the --version option description.
    /// </summary>
    /// <returns>
    /// Localized description string.
    /// </returns>
    public static string CmdVersionDescription => Get();

    /// <summary>
    /// Gets the "Blank" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColBlank => Get();

    /// <summary>
    /// Gets the "Code" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColCode => Get();

    /// <summary>
    /// Gets the "Comment" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColComment => Get();

    /// <summary>
    /// Gets the "File" column header (singular, used in by-file table).
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColFile => Get();

    /// <summary>
    /// Gets the "Files" column header (plural, used in by-language table).
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColFiles => Get();

    /// <summary>
    /// Gets the "Comment Health" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColHealth => Get();

    /// <summary>
    /// Gets the "Language" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColLanguage => Get();

    // -------------------------------------------------------------------------
    // Table column headers
    // -------------------------------------------------------------------------
    /// <summary>
    /// Gets the "Path" column header (used in the skipped-files table).
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColPath => Get();

    /// <summary>
    /// Gets the "Reason" column header (used in the skipped-files table).
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColReason => Get();

    /// <summary>
    /// Gets the "Total" column header.
    /// </summary>
    /// <returns>
    /// Localized header string.
    /// </returns>
    public static string ColTotal => Get();

    /// <summary>
    /// Gets the "Dense" health label (≥40% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthDense => Get();

    /// <summary>
    /// Gets the "Fair" health label (&lt;10% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthFair => Get();

    /// <summary>
    /// Gets the "Good" health label (&lt;25% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthGood => Get();

    /// <summary>
    /// Gets the "High" health label (&lt;40% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthHigh => Get();

    /// <summary>
    /// Gets the "Low" health label (&lt;5% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthLow => Get();

    /// <summary>
    /// Gets the "None" health label (0% comment density).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HealthNone => Get();

    /// <summary>
    /// Gets the "By File" section heading.
    /// </summary>
    /// <returns>
    /// Localized heading string.
    /// </returns>
    public static string HtmlByFile => Get();

    /// <summary>
    /// Gets the "By Language" section heading.
    /// </summary>
    /// <returns>
    /// Localized heading string.
    /// </returns>
    public static string HtmlByLanguage => Get();

    /// <summary>
    /// Gets the "Collapse All" button label.
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HtmlCollapseAll => Get();

    /// <summary>
    /// Gets the "Expand All" button label.
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HtmlExpandAll => Get();

    /// <summary>
    /// Gets the unit word that follows the file count (e.g. "files" / "个文件").
    /// </summary>
    /// <returns>
    /// Localized unit string.
    /// </returns>
    public static string HtmlFilesUnit => Get();

    /// <summary>
    /// Gets the "Generated:" meta label prefix.
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HtmlGenerated => Get();

    /// <summary>
    /// Gets the root folder label shown in the by-file tree.
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string HtmlRoot => Get();

    /// <summary>
    /// Gets the HTML document and page heading title.
    /// </summary>
    /// <returns>
    /// Localized title string.
    /// </returns>
    public static string HtmlTitle => Get();

    // -------------------------------------------------------------------------
    // HTML content
    // -------------------------------------------------------------------------
    /// <summary>
    /// Gets the "Skipped Files" section heading.
    /// </summary>
    /// <returns>
    /// Localized heading string.
    /// </returns>
    public static string HtmlSkippedFiles => Get();

    /// <summary>
    /// Gets the unit phrase that follows the total line count (e.g. "total lines" / "行合计").
    /// </summary>
    /// <returns>
    /// Localized unit string.
    /// </returns>
    public static string HtmlTotalLines => Get();

    /// <summary>
    /// Gets the "Analyzing" progress label (no punctuation).
    /// </summary>
    /// <returns>
    /// Localized label string.
    /// </returns>
    public static string MsgAnalyzing => Get();

    // -------------------------------------------------------------------------
    // Status / error messages
    // -------------------------------------------------------------------------
    /// <summary>
    /// Gets the live-table progress caption format string.
    /// Positional arguments: {0} completed count, {1} total count.
    /// </summary>
    /// <returns>
    /// Localized format string.
    /// </returns>
    public static string MsgAnalyzingProgress => Get();

    /// <summary>
    /// Gets the "No files matched." message.
    /// </summary>
    /// <returns>
    /// Localized message string.
    /// </returns>
    public static string MsgNoFilesMatched => Get();

    /// <summary>
    /// Gets the "Saved to:" message prefix.
    /// </summary>
    /// <returns>
    /// Localized message string.
    /// </returns>
    public static string MsgSavedTo => Get();

    /// <summary>
    /// Gets the skipped-file error format string.
    /// Positional arguments: {0} file path, {1} exception message.
    /// </summary>
    /// <returns>
    /// Localized format string.
    /// </returns>
    public static string MsgSkipped => Get();

    /// <summary>
    /// Gets the by-file pagination prompt format string.
    /// Positional arguments: {0} rows shown so far, {1} total rows.
    /// The value may contain Spectre.Console markup (e.g. [bold]Q[/]).
    /// </summary>
    /// <returns>
    /// Localized format string.
    /// </returns>
    public static string PaginationPrompt => Get();

    /// <summary>
    /// Gets the "Total" summary row label.
    /// </summary>
    /// <returns>
    /// Localized row label string.
    /// </returns>
    public static string RowTotal => Get();

    /// <summary>
    /// Returns the localized string whose resource key matches the calling member name.
    /// Falls back to the key itself when no value is found.
    /// </summary>
    /// <param name="key">Filled automatically by <see cref="CallerMemberNameAttribute"/>.</param>
    /// <returns>
    /// The localized string for the current <see cref="CultureInfo.CurrentUICulture"/>.
    /// </returns>
    private static string Get([CallerMemberName] string? key = null) =>
        Rm.GetString(key!, CultureInfo.CurrentUICulture) ?? key!;
}