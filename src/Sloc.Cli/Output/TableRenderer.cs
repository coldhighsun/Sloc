using Sloc.Core.Languages;
using Sloc.Core.Models;
using Spectre.Console;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as colored tables using Spectre.Console.
/// </summary>
public sealed class TableRenderer : IResultRenderer
{
    private static readonly IReadOnlyDictionary<string, bool> CommentSupportByLanguage =
            LanguageRegistry.Languages.ToDictionary(
                l => l.Name,
                l => l.ShowHealth && (l.LineCommentTokens.Count > 0 || l.BlockComments.Count > 0),
                StringComparer.OrdinalIgnoreCase);

    private readonly record struct DisplayItem(bool IsFolder, string FolderPath, FileAnalysis? File, string TreePrefix = "");

    /// <inheritdoc />
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.FileCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No files matched.[/]");
            return;
        }

        if (byFile)
        {
            RenderByFile(summary, noHealth);
        }
        else
        {
            RenderByLanguage(summary, noHealth);
        }
    }

    internal static Table BuildLanguageTable(AnalysisSummary summary, string? caption = null, bool noHealth = false)
    {
        var table = new Table().Border(TableBorder.Rounded);

        if (caption is not null)
        {
            table.Caption(new TableTitle(caption));
        }

        table.AddColumn(CliResources.ColLanguage);
        table.AddColumn(new TableColumn(CliResources.ColFiles).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColCode).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColComment).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColBlank).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColTotal).RightAligned());
        if (!noHealth)
        {
            table.AddColumn(CliResources.ColHealth);
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
                    BuildHealthCell(language));
            }
        }

        table.AddEmptyRow();
        var totalCodeCell = noHealth ? $"[bold]{summary.Code:N0}[/]" : WithPercent(summary.Code, summary.Total, bold: true);
        var totalCommentCell = noHealth ? $"[bold]{summary.Comment:N0}[/]" : WithPercent(summary.Comment, summary.Total, bold: true);
        var totalBlankCell = noHealth ? $"[bold]{summary.Blank:N0}[/]" : WithPercent(summary.Blank, summary.Total, bold: true);
        if (noHealth)
        {
            table.AddRow(
                $"[bold]{CliResources.RowTotal}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]");
        }
        else
        {
            table.AddRow(
                $"[bold]{CliResources.RowTotal}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]",
                "[grey]—[/]");
        }

        return table;
    }

    internal static void RenderByFile(AnalysisSummary summary, bool noHealth, bool paged = false)
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

    private static void AddFileRow(Table table, FileAnalysis file, bool noHealth, bool indented = false, string treePrefix = "")
    {
        var hasCommentSupport = CommentSupportByLanguage.TryGetValue(file.Language, out var supports) && supports;
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
                BuildHealthCell(file.Code, file.Comment, hasCommentSupport));
        }
    }

    private static void AddFileTotalRow(Table table, AnalysisSummary summary, bool noHealth)
    {
        table.AddEmptyRow();
        var totalCodeCell = noHealth ? $"[bold]{summary.Code:N0}[/]" : WithPercent(summary.Code, summary.Total, bold: true);
        var totalCommentCell = noHealth ? $"[bold]{summary.Comment:N0}[/]" : WithPercent(summary.Comment, summary.Total, bold: true);
        var totalBlankCell = noHealth ? $"[bold]{summary.Blank:N0}[/]" : WithPercent(summary.Blank, summary.Total, bold: true);
        if (noHealth)
        {
            table.AddRow(
                $"[bold]{CliResources.RowTotal}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]");
        }
        else
        {
            table.AddRow(
                $"[bold]{CliResources.RowTotal}[/]",
                $"[bold]{summary.FileCount:N0}[/]",
                totalCodeCell,
                totalCommentCell,
                totalBlankCell,
                $"[bold]{summary.Total:N0}[/]",
                "[grey]—[/]");
        }
    }

    private static void AddFolderHeaderRow(Table table, string folder, bool noHealth, string treePrefix = "")
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

    private static List<DisplayItem> BuildGroupedItems(IReadOnlyList<FileAnalysis> files)
    {
        var root = BuildTree(files);
        var items = new List<DisplayItem>(files.Count + 32);
        for (var i = 0; i < root.Children.Count; i++)
        {
            FlattenNode(root.Children[i], "", i == root.Children.Count - 1, items);
        }
        return items;
    }

    private static string BuildHealthCell(LanguageStatistics stats)
    {
        var hasCommentSupport = CommentSupportByLanguage.TryGetValue(stats.Language, out var supports) && supports;
        return BuildHealthCell(stats.Code, stats.Comment, hasCommentSupport);
    }

    private static string BuildHealthCell(int code, int comment, bool hasCommentSupport)
    {
        if (!hasCommentSupport)
        {
            return "[grey]—[/]";
        }

        var codeAndComment = code + comment;
        if (codeAndComment == 0)
        {
            return "[grey]—[/]";
        }

        var density = (double)comment / codeAndComment;
        var (color, label) = density switch
        {
            <= 0.0 => ("red", CliResources.HealthNone),
            < 0.05 => ("red", CliResources.HealthLow),
            < 0.10 => ("yellow", CliResources.HealthFair),
            < 0.25 => ("green", CliResources.HealthGood),
            < 0.40 => ("yellow", CliResources.HealthHigh),
            _ => ("red", CliResources.HealthDense)
        };

        return $"[{color}]■ {label}[/]";
    }

    private static TreeNode BuildTree(IReadOnlyList<FileAnalysis> files)
    {
        var root = new TreeNode { IsFolder = true };
        var sortedFiles = files
            .Select(f => (Relative: ToRelative(f.Path), File: f))
            .OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, file) in sortedFiles)
        {
            var dirPart = Path.GetDirectoryName(relative) ?? string.Empty;
            var segments = string.IsNullOrEmpty(dirPart)
                ? Array.Empty<string>()
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

    private static Table CreateFileTable(bool noHealth)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(CliResources.ColFile);
        table.AddColumn(CliResources.ColLanguage);
        table.AddColumn(new TableColumn(CliResources.ColCode).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColComment).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColBlank).RightAligned());
        table.AddColumn(new TableColumn(CliResources.ColTotal).RightAligned());
        if (!noHealth)
        {
            table.AddColumn(CliResources.ColHealth);
        }
        return table;
    }

    private static void FlattenNode(TreeNode node, string prefix, bool isLast, List<DisplayItem> items)
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

    private static void RenderByFilePaged(AnalysisSummary summary, List<DisplayItem> grouped, int pageSize, bool noHealth)
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
                AnsiConsole.Markup($"[grey]{string.Format(CliResources.PaginationPrompt, filesShown, total)}[/] ");
                var key = Console.ReadKey(intercept: true);
                AnsiConsole.WriteLine();
                if (key.Key == ConsoleKey.Q)
                {
                    break;
                }
            }
        }
    }

    private static void RenderByLanguage(AnalysisSummary summary, bool noHealth)
    {
        AnsiConsole.Write(BuildLanguageTable(summary, noHealth: noHealth));
    }

    private static bool ShouldPaginate(int fileCount, out int pageSize)
    {
        const int overhead = 6;
        if (Console.IsOutputRedirected || Console.WindowHeight <= 0)
        {
            pageSize = 0;
            return false;
        }

        pageSize = Math.Max(1, Console.WindowHeight - overhead);
        return fileCount > pageSize;
    }

    private static string ToRelative(string path)
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

    private static string WithPercent(int count, int total, bool bold = false)
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
        } = new();

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