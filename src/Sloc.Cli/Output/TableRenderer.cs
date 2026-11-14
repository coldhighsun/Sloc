using Sloc.Core.Models;
using Spectre.Console;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as colored tables using Spectre.Console.
/// </summary>
public sealed class TableRenderer : IResultRenderer
{
    private readonly record struct DisplayItem(bool IsFolder, string FolderPath, FileAnalysis? File, string TreePrefix = "");

    /// <inheritdoc />
    /// <remarks>
    /// Table-specific capabilities that the shared <see cref="IResultRenderer"/> signature
    /// cannot express — pagination and the live-refreshing progress table — are not
    /// available through this method; <see cref="Analysis.AnalyzeHandler"/> calls
    /// <see cref="RenderByFile"/> and <see cref="BuildLanguageTable"/> directly for those.
    /// <paramref name="detailed"/> has no Table equivalent (a table shows either the
    /// by-language or by-file view, never both) and is ignored, matching how the other
    /// renderers treat it as meaningless for this format. <paramref name="sourcePath"/> is
    /// also ignored here; <see cref="Analysis.AnalyzeHandler"/> prints the analyzed path as a
    /// separate banner line above the table instead.
    /// </remarks>
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false, string? sourcePath = null, bool noComplexity = false)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.FileCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files matched.[/]");
        }
        else if (byFile)
        {
            RenderByFile(summary, noHealth, noComplexity: noComplexity);
        }
        else
        {
            AnsiConsole.Write(BuildLanguageTable(summary, noHealth: noHealth, noComplexity: noComplexity));
        }

        RenderSkipped(summary);
    }

    internal Table BuildLanguageTable(AnalysisSummary summary, string? caption = null, bool noHealth = false, bool noComplexity = false)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Theme.Border);

        var note = ColumnLayout.TruncationNote(summary) is { } text ? $"[grey]{Markup.Escape(text)}[/]" : null;
        var fullCaption = caption is not null && note is not null ? $"{caption}{Environment.NewLine}{note}" : caption ?? note;
        if (fullCaption is not null)
        {
            table.Caption(new TableTitle(fullCaption));
        }

        table.AddColumn(Theme.Header("Language"));
        table.AddColumn(new TableColumn(Theme.Header("Files")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Code")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Comment")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Blank")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Total")).RightAligned());
        if (!noHealth)
        {
            table.AddColumn(Theme.Header("Comment Health"));
        }
        if (!noComplexity)
        {
            table.AddColumn(new TableColumn(Theme.Header("Complexity")).RightAligned());
        }

        foreach (var language in summary.ByLanguage)
        {
            var codeCell = Count(language.Code, language.Total, noHealth);
            var commentCell = Count(language.Comment, language.Total, noHealth, Theme.CommentStyle);
            var blankCell = Count(language.Blank, language.Total, noHealth, Theme.BlankStyle);
            var row = new List<string>
            {
                Theme.Language(language.Language),
                language.Files.ToString("N0"),
                codeCell,
                commentCell,
                blankCell,
                language.Total.ToString("N0")
            };
            if (!noHealth)
            {
                row.Add(BuildHealthCell(language.Health));
            }
            if (!noComplexity)
            {
                row.Add(BuildComplexityCell(language.ComplexityTotal));
            }

            table.AddRow(row.ToArray());
        }

        table.AddEmptyRow();
        var totalCodeCell = Count(summary.Code, summary.Total, noHealth, bold: true);
        var totalCommentCell = Count(summary.Comment, summary.Total, noHealth, Theme.CommentStyle, bold: true);
        var totalBlankCell = Count(summary.Blank, summary.Total, noHealth, Theme.BlankStyle, bold: true);
        var totalRow = new List<string>
        {
            $"[bold]{"Total"}[/]",
            $"[bold]{summary.FileCount:N0}[/]",
            totalCodeCell,
            totalCommentCell,
            totalBlankCell,
            $"[bold]{summary.Total:N0}[/]"
        };
        if (!noHealth)
        {
            totalRow.Add("[grey]—[/]");
        }
        if (!noComplexity)
        {
            totalRow.Add(BuildComplexityCell(summary.ComplexityTotal, bold: true));
        }

        table.AddRow(totalRow.ToArray());

        return table;
    }

    internal void RenderByFile(AnalysisSummary summary, bool noHealth, bool paged = false, bool noComplexity = false)
    {
        var files = summary.Files;
        var grouped = BuildGroupedItems(files);
        if (paged && ShouldPaginate(files.Count, out var pageSize))
        {
            RenderByFilePaged(summary, grouped, pageSize, noHealth, noComplexity);
        }
        else
        {
            AnsiConsole.Write(BuildFileTable(summary, grouped, noHealth, noComplexity));
        }
    }

    /// <summary>
    /// Builds the unpaged by-file table: every file grouped under its folder tree, followed
    /// by the total row.
    /// </summary>
    /// <param name="summary">The analysis summary to render.</param>
    /// <param name="noHealth">Whether to omit the Comment Health column.</param>
    /// <param name="noComplexity">Whether to omit the Complexity column.</param>
    /// <returns>The table, ready to write to a console.</returns>
    internal Table BuildFileTable(AnalysisSummary summary, bool noHealth, bool noComplexity = false) =>
        BuildFileTable(summary, BuildGroupedItems(summary.Files), noHealth, noComplexity);

    /// <summary>
    /// Builds the unpaged by-file table from already-grouped display items.
    /// </summary>
    /// <param name="summary">The analysis summary, for the total row.</param>
    /// <param name="grouped">The folder and file rows, in display order.</param>
    /// <param name="noHealth">Whether to omit the Comment Health column.</param>
    /// <param name="noComplexity">Whether to omit the Complexity column.</param>
    /// <returns>The table, ready to write to a console.</returns>
    private Table BuildFileTable(AnalysisSummary summary, List<DisplayItem> grouped, bool noHealth, bool noComplexity)
    {
        var table = CreateFileTable(noHealth, noComplexity);
        foreach (var item in grouped)
        {
            if (item.IsFolder)
            {
                AddFolderHeaderRow(table, item.FolderPath, noHealth, noComplexity, item.TreePrefix);
            }
            else
            {
                AddFileRow(table, item.File!, noHealth, noComplexity, indented: true, item.TreePrefix);
            }
        }
        AddFileTotalRow(table, summary, noHealth, noComplexity);
        return table;
    }

    internal void RenderSkipped(AnalysisSummary summary)
    {
        if (summary.Skipped.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
        table.Title(new TableTitle($"[bold yellow]{Markup.Escape("Skipped Files")} ({summary.Skipped.Count:N0})[/]"));
        table.AddColumn(Theme.Header("Path"));
        table.AddColumn(Theme.Header("Reason"));
        foreach (var entry in summary.Skipped)
        {
            table.AddRow(Markup.Escape(entry.Path), $"[yellow]{Markup.Escape(entry.Reason)}[/]");
        }

        AnsiConsole.Write(table);
    }

    private void AddFileRow(Table table, FileAnalysis file, bool noHealth, bool noComplexity, bool indented = false, string treePrefix = "")
    {
        var codeCell = Count(file.Code, file.Total, noHealth);
        var commentCell = Count(file.Comment, file.Total, noHealth, Theme.CommentStyle);
        var blankCell = Count(file.Blank, file.Total, noHealth, Theme.BlankStyle);
        var fileCell = indented
            ? $"{TreePrefix(treePrefix)}{Markup.Escape(Path.GetFileName(file.Path))}"
            : Markup.Escape(ToRelative(file.Path));
        var row = new List<string>
        {
            fileCell,
            Theme.Language(file.Language),
            codeCell,
            commentCell,
            blankCell,
            file.Total.ToString("N0")
        };
        if (!noHealth)
        {
            row.Add(BuildHealthCell(file.Health));
        }
        if (!noComplexity)
        {
            row.Add(BuildComplexityCell(file.Complexity));
        }

        table.AddRow(row.ToArray());
    }

    private void AddFileTotalRow(Table table, AnalysisSummary summary, bool noHealth, bool noComplexity)
    {
        table.AddEmptyRow();
        var totalCodeCell = Count(summary.Code, summary.Total, noHealth, bold: true);
        var totalCommentCell = Count(summary.Comment, summary.Total, noHealth, Theme.CommentStyle, bold: true);
        var totalBlankCell = Count(summary.Blank, summary.Total, noHealth, Theme.BlankStyle, bold: true);
        var row = new List<string>
        {
            $"[bold]{"Total"}[/]",
            $"[bold]{summary.FileCount:N0}[/]",
            totalCodeCell,
            totalCommentCell,
            totalBlankCell,
            $"[bold]{summary.Total:N0}[/]"
        };
        if (!noHealth)
        {
            row.Add("[grey]—[/]");
        }
        if (!noComplexity)
        {
            row.Add(BuildComplexityCell(summary.ComplexityTotal, bold: true));
        }

        table.AddRow(row.ToArray());
    }

    private void AddFolderHeaderRow(Table table, string folder, bool noHealth, bool noComplexity, string treePrefix = "")
    {
        // A drive root (e.g. "C:", for files on a different drive than the current directory)
        // has no file name of its own, so it is labeled with the whole root instead.
        var name = Path.GetFileName(folder) is { Length: > 0 } folderName ? folderName : folder;
        var label = string.IsNullOrEmpty(folder)
            ? $"{TreePrefix(treePrefix)}[grey].[/]"
            : $"{TreePrefix(treePrefix)}[{Theme.FolderStyle}]📁 {Markup.Escape(name)}[/]";
        var columnCount = 5 + (noHealth ? 0 : 1) + (noComplexity ? 0 : 1);
        var row = new string[columnCount + 1];
        row[0] = label;
        for (var i = 1; i < row.Length; i++)
        {
            row[i] = string.Empty;
        }

        table.AddRow(row);
    }

    private string BuildComplexityCell(int? complexity, bool bold = false)
    {
        if (complexity is not { } value)
        {
            return "[grey]—[/]";
        }

        return bold ? $"[bold]{value:N0}[/]" : value.ToString("N0");
    }

    private List<DisplayItem> BuildGroupedItems(IReadOnlyList<FileAnalysis> files)
    {
        var root = BuildTree(files);
        var items = new List<DisplayItem>(files.Count + 32);
        for (var i = 0; i < root.Children.Count; i++)
        {
            FlattenNode(root.Children[i], "", i == root.Children.Count - 1, items);
        }
        return items;
    }

    private string BuildHealthCell(CommentHealthLevel health)
    {
        if (health == CommentHealthLevel.NotApplicable)
        {
            return "[grey]—[/]";
        }

        var color = health switch
        {
            CommentHealthLevel.None => "red",
            CommentHealthLevel.Low => "red",
            CommentHealthLevel.Fair => "yellow",
            CommentHealthLevel.Good => "green",
            CommentHealthLevel.High => "cyan",
            CommentHealthLevel.Dense => "magenta",
            _ => "red"
        };

        return $"[{color}]■ {health}[/]";
    }

    private TreeNode BuildTree(IReadOnlyList<FileAnalysis> files)
    {
        var root = new TreeNode { IsFolder = true };
        var sortedFiles = files
            .Select(f => (Relative: ToRelative(f.Path), File: f))
            .OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, file) in sortedFiles)
        {
            var dirPart = Path.GetDirectoryName(relative) ?? string.Empty;
            var segments = string.IsNullOrEmpty(dirPart)
                ? []
                : dirPart.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                // Ordinal: folders differing only by case (possible in a --git-hash tree from a
                // case-sensitive filesystem) are distinct and must not be merged.
                var existing = current.Children.Find(c => c.IsFolder && string.Equals(c.Name, seg, StringComparison.Ordinal));
                if (existing is null)
                {
                    var folderPath = string.Join(Path.DirectorySeparatorChar.ToString(), segments, 0, i + 1);
                    existing = new TreeNode { IsFolder = true, Name = seg, FolderPath = folderPath };
                    current.Children.Add(existing);
                }
                current = existing;
            }
            current.Children.Add(new TreeNode
            {
                IsFolder = false,
                Name = Path.GetFileName(file.Path),
                File = file
            });
        }
        return root;
    }

    private Table CreateFileTable(bool noHealth, bool noComplexity = false)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Theme.Border);
        table.AddColumn(Theme.Header("File"));
        table.AddColumn(Theme.Header("Language"));
        table.AddColumn(new TableColumn(Theme.Header("Code")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Comment")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Blank")).RightAligned());
        table.AddColumn(new TableColumn(Theme.Header("Total")).RightAligned());
        if (!noHealth)
        {
            table.AddColumn(Theme.Header("Comment Health"));
        }
        if (!noComplexity)
        {
            table.AddColumn(new TableColumn(Theme.Header("Complexity")).RightAligned());
        }
        return table;
    }

    private void FlattenNode(TreeNode node, string prefix, bool isLast, List<DisplayItem> items)
    {
        var connector = isLast ? "└── " : "├── ";
        var treePrefix = prefix + connector;
        items.Add(node.IsFolder
            ? new DisplayItem(true, node.FolderPath, null, treePrefix)
            : new DisplayItem(false, string.Empty, node.File, treePrefix));
        if (node.IsFolder)
        {
            var childPrefix = prefix + (isLast ? "    " : "│   ");
            for (var i = 0; i < node.Children.Count; i++)
            {
                FlattenNode(node.Children[i], childPrefix, i == node.Children.Count - 1, items);
            }
        }
    }

    private void RenderByFilePaged(AnalysisSummary summary, List<DisplayItem> grouped, int pageSize, bool noHealth, bool noComplexity)
    {
        var total = summary.Files.Count;
        var filesShown = 0;
        var itemIndex = 0;
        while (itemIndex < grouped.Count)
        {
            var table = CreateFileTable(noHealth, noComplexity);
            var filesInPage = 0;
            while (itemIndex < grouped.Count && filesInPage < pageSize)
            {
                var item = grouped[itemIndex++];
                if (item.IsFolder)
                {
                    AddFolderHeaderRow(table, item.FolderPath, noHealth, noComplexity, item.TreePrefix);
                }
                else
                {
                    AddFileRow(table, item.File!, noHealth, noComplexity, indented: true, item.TreePrefix);
                    filesInPage++;
                }
            }
            filesShown += filesInPage;
            if (itemIndex >= grouped.Count)
            {
                AddFileTotalRow(table, summary, noHealth, noComplexity);
            }
            AnsiConsole.Write(table);

            if (filesShown < total)
            {
                if (Console.IsInputRedirected)
                {
                    continue;
                }

                AnsiConsole.Markup($"[grey]-- {filesShown}/{total} shown, press any key to continue, [bold]Q[/] to stop --[/] ");
                var key = Console.ReadKey(intercept: true);
                AnsiConsole.WriteLine();
                if (key.Key == ConsoleKey.Q)
                {
                    break;
                }
            }
        }
    }

    private bool ShouldPaginate(int fileCount, out int pageSize)
    {
        const int overhead = 6;
        if (Console.IsOutputRedirected)
        {
            pageSize = 0;
            return false;
        }

        int windowHeight;
        try
        {
            windowHeight = Console.WindowHeight;
        }
        catch (Exception)
        {
            // Some terminals/hosts (no attached console, certain Windows shells) throw when
            // querying the window size; treat that the same as "can't paginate".
            pageSize = 0;
            return false;
        }

        if (windowHeight <= 0)
        {
            pageSize = 0;
            return false;
        }

        pageSize = Math.Max(1, windowHeight - overhead);
        return fileCount > pageSize;
    }

    private string ToRelative(string path)
    {
        try
        {
            return Path.GetRelativePath(Environment.CurrentDirectory, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Colors the tree connectors of a by-file row; the plain prefix is only box-drawing characters and spaces.</summary>
    private static string TreePrefix(string prefix) =>
        prefix.Length == 0 ? prefix : $"[{Theme.TreeStyle}]{prefix}[/]";

    /// <summary>
    /// Formats a count cell: the number in <paramref name="style"/> (and bold when requested),
    /// followed by its share of <paramref name="total"/> unless <paramref name="noHealth"/> hides it.
    /// </summary>
    private static string Count(int count, int total, bool noHealth, string? style = null, bool bold = false)
    {
        var tag = (bold ? "bold " : "") + style;
        tag = tag.Trim();
        var number = count.ToString("N0");
        var text = tag.Length == 0 ? number : $"[{tag}]{number}[/]";
        if (noHealth || total == 0)
        {
            return text;
        }

        var pct = $"{(double)count / total * 100,3:F0}%";
        return $"{text} [{Theme.PercentStyle}]({pct})[/]";
    }

    private sealed class TreeNode
    {
        public List<TreeNode> Children
        {
            get;
        } = [];

        public FileAnalysis? File
        {
            get; init;
        }

        public string FolderPath
        {
            get; init;
        } = string.Empty;

        public bool IsFolder
        {
            get; init;
        }

        public string Name
        {
            get; init;
        } = string.Empty;
    }
}