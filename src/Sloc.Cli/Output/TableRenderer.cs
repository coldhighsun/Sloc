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
    /// available through this method; <see cref="AnalyzeHandler"/> calls
    /// <see cref="RenderByFile"/> and <see cref="BuildLanguageTable"/> directly for those.
    /// <paramref name="detailed"/> has no Table equivalent (a table shows either the
    /// by-language or by-file view, never both) and is ignored, matching how the other
    /// renderers treat it as meaningless for this format. <paramref name="sourcePath"/> is
    /// also ignored here; <see cref="AnalyzeHandler"/> prints the analyzed path as a
    /// separate banner line above the table instead.
    /// </remarks>
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth, bool detailed = false, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.FileCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files matched.[/]");
        }
        else if (byFile)
        {
            RenderByFile(summary, noHealth);
        }
        else
        {
            AnsiConsole.Write(BuildLanguageTable(summary, noHealth: noHealth));
        }

        RenderSkipped(summary);
    }

    internal Table BuildLanguageTable(AnalysisSummary summary, string? caption = null, bool noHealth = false)
    {
        var table = new Table().Border(TableBorder.Rounded);

        if (caption is not null)
        {
            table.Caption(new TableTitle(caption));
        }

        table.AddColumn("Language");
        table.AddColumn(new TableColumn("Files").RightAligned());
        table.AddColumn(new TableColumn("Code").RightAligned());
        table.AddColumn(new TableColumn("Comment").RightAligned());
        table.AddColumn(new TableColumn("Blank").RightAligned());
        table.AddColumn(new TableColumn("Total").RightAligned());
        if (!noHealth)
        {
            table.AddColumn("Comment Health");
        }

        foreach (var language in summary.ByLanguage)
        {
            var codeCell = noHealth ? language.Code.ToString("N0") : WithPercent(language.Code, language.Total);
            var commentCell = noHealth ? language.Comment.ToString("N0") : WithPercent(language.Comment, language.Total);
            var blankCell = noHealth ? language.Blank.ToString("N0") : WithPercent(language.Blank, language.Total);
            if (noHealth)
            {
                table.AddRow(
                    Markup.Escape(language.Language),
                    language.Files.ToString("N0"),
                    codeCell,
                    commentCell,
                    blankCell,
                    language.Total.ToString("N0"));
            }
            else
            {
                table.AddRow(
                    Markup.Escape(language.Language),
                    language.Files.ToString("N0"),
                    codeCell,
                    commentCell,
                    blankCell,
                    language.Total.ToString("N0"),
                    BuildHealthCell(language.Health));
            }
        }

        table.AddEmptyRow();
        var totalCodeCell = noHealth ? $"[bold]{summary.Code:N0}[/]" : WithPercent(summary.Code, summary.Total, bold: true);
        var totalCommentCell = noHealth ? $"[bold]{summary.Comment:N0}[/]" : WithPercent(summary.Comment, summary.Total, bold: true);
        var totalBlankCell = noHealth ? $"[bold]{summary.Blank:N0}[/]" : WithPercent(summary.Blank, summary.Total, bold: true);
        if (noHealth)
        {
            table.AddRow(
                $"[bold]{"Total"}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]");
        }
        else
        {
            table.AddRow(
                $"[bold]{"Total"}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]",
                "[grey]—[/]");
        }

        return table;
    }

    internal void RenderByFile(AnalysisSummary summary, bool noHealth, bool paged = false)
    {
        var files = summary.Files;
        var grouped = BuildGroupedItems(files);
        if (paged && ShouldPaginate(files.Count, out var pageSize))
        {
            RenderByFilePaged(summary, grouped, pageSize, noHealth);
        }
        else
        {
            var table = CreateFileTable(noHealth);
            foreach (var item in grouped)
            {
                if (item.IsFolder)
                {
                    AddFolderHeaderRow(table, item.FolderPath, noHealth, item.TreePrefix);
                }
                else
                {
                    AddFileRow(table, item.File!, noHealth, indented: true, item.TreePrefix);
                }
            }
            AddFileTotalRow(table, summary, noHealth);
            AnsiConsole.Write(table);
        }
    }

    internal void RenderSkipped(AnalysisSummary summary)
    {
        if (summary.Skipped.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.Title(new TableTitle($"[yellow]{Markup.Escape("Skipped Files")} ({summary.Skipped.Count:N0})[/]"));
        table.AddColumn("Path");
        table.AddColumn("Reason");
        foreach (var entry in summary.Skipped)
        {
            table.AddRow(Markup.Escape(entry.Path), Markup.Escape(entry.Reason));
        }

        AnsiConsole.Write(table);
    }

    private void AddFileRow(Table table, FileAnalysis file, bool noHealth, bool indented = false, string treePrefix = "")
    {
        var codeCell = noHealth ? file.Code.ToString("N0") : WithPercent(file.Code, file.Total);
        var commentCell = noHealth ? file.Comment.ToString("N0") : WithPercent(file.Comment, file.Total);
        var blankCell = noHealth ? file.Blank.ToString("N0") : WithPercent(file.Blank, file.Total);
        var fileCell = indented
            ? $"{treePrefix}{Markup.Escape(Path.GetFileName(file.Path))}"
            : Markup.Escape(ToRelative(file.Path));
        if (noHealth)
        {
            table.AddRow(
                fileCell,
                Markup.Escape(file.Language),
                codeCell,
                commentCell,
                blankCell,
                file.Total.ToString("N0"));
        }
        else
        {
            table.AddRow(
                fileCell,
                Markup.Escape(file.Language),
                codeCell,
                commentCell,
                blankCell,
                file.Total.ToString("N0"),
                BuildHealthCell(file.Health));
        }
    }

    private void AddFileTotalRow(Table table, AnalysisSummary summary, bool noHealth)
    {
        table.AddEmptyRow();
        var totalCodeCell = noHealth ? $"[bold]{summary.Code:N0}[/]" : WithPercent(summary.Code, summary.Total, bold: true);
        var totalCommentCell = noHealth ? $"[bold]{summary.Comment:N0}[/]" : WithPercent(summary.Comment, summary.Total, bold: true);
        var totalBlankCell = noHealth ? $"[bold]{summary.Blank:N0}[/]" : WithPercent(summary.Blank, summary.Total, bold: true);
        if (noHealth)
        {
            table.AddRow(
                $"[bold]{"Total"}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]");
        }
        else
        {
            table.AddRow(
                $"[bold]{"Total"}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]",
                "[grey]—[/]");
        }
    }

    private void AddFolderHeaderRow(Table table, string folder, bool noHealth, string treePrefix = "")
    {
        var label = string.IsNullOrEmpty(folder)
            ? $"{treePrefix}[grey].[/]"
            : $"{treePrefix}[bold]📁 {Markup.Escape(Path.GetFileName(folder))}[/]";
        if (noHealth)
        {
            table.AddRow(label, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
        else
        {
            table.AddRow(label, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
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
                var existing = current.Children.Find(c => c.IsFolder && string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase));
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

    private Table CreateFileTable(bool noHealth)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("File");
        table.AddColumn("Language");
        table.AddColumn(new TableColumn("Code").RightAligned());
        table.AddColumn(new TableColumn("Comment").RightAligned());
        table.AddColumn(new TableColumn("Blank").RightAligned());
        table.AddColumn(new TableColumn("Total").RightAligned());
        if (!noHealth)
        {
            table.AddColumn("Comment Health");
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

    private void RenderByFilePaged(AnalysisSummary summary, List<DisplayItem> grouped, int pageSize, bool noHealth)
    {
        var total = summary.Files.Count;
        var filesShown = 0;
        var itemIndex = 0;
        while (itemIndex < grouped.Count)
        {
            var table = CreateFileTable(noHealth);
            var filesInPage = 0;
            while (itemIndex < grouped.Count && filesInPage < pageSize)
            {
                var item = grouped[itemIndex++];
                if (item.IsFolder)
                {
                    AddFolderHeaderRow(table, item.FolderPath, noHealth, item.TreePrefix);
                }
                else
                {
                    AddFileRow(table, item.File!, noHealth, indented: true, item.TreePrefix);
                    filesInPage++;
                }
            }
            filesShown += filesInPage;
            if (itemIndex >= grouped.Count)
            {
                AddFileTotalRow(table, summary, noHealth);
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

    private string WithPercent(int count, int total, bool bold = false)
    {
        if (total == 0)
        {
            return bold ? $"[bold]{count:N0}[/]" : count.ToString("N0");
        }

        var pct = $"{(double)count / total * 100,3:F0}%";
        return bold
            ? $"[bold]{count:N0}[/] [grey]({pct})[/]"
            : $"{count:N0} [grey]({pct})[/]";
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