using System.Globalization;
using System.Net;
using System.Text;
using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Cli.Output;

/// <summary>
/// Renders analysis results as a self-contained HTML document.
/// </summary>
public sealed class HtmlRenderer : IResultRenderer
{
    private const string Css = """
        *, *::before, *::after { box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Arial, sans-serif;
            margin: 0;
            padding: 16px;
            width: 100%;
            color: #24292e; line-height: 1.5; background: #ffffff;
        }
        h1 { font-size: 1.75rem; border-bottom: 1px solid #e1e4e8; padding-bottom: 10px; margin: 0 0 4px 0; }
        h2 { font-size: 1.1rem; margin-top: 32px; margin-bottom: 10px; }
        .meta { color: #586069; font-size: .875rem; margin-bottom: 24px; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 28px; font-size: .875rem; }
        th { background: #f6f8fa; border: 1px solid #d0d7de; padding: 8px 12px; white-space: nowrap; font-weight: 600; }
        td { border: 1px solid #d0d7de; padding: 8px 12px; }
        tr:hover > td { background: #f6f8fa; }
        tfoot > tr > td { font-weight: 700; background: #f6f8fa; }
        .r { text-align: right; }
        .pct { color: #8c8c8c; font-size: .8em; margin-left: 4px; }
        .h-none, .h-low { color: #cf222e; }
        .h-fair, .h-high { color: #9a6700; }
        .h-good { color: #1a7f37; }
        .h-dense { color: #cf222e; }
        .h-na { color: #8c8c8c; }
        .file-controls { display: flex; gap: 8px; margin-bottom: 12px; }
        .control-btn {
            appearance: none;
            border: 1px solid #d0d7de;
            background: #f6f8fa;
            color: #24292e;
            border-radius: 6px;
            font-size: .875rem;
            padding: 6px 10px;
            cursor: pointer;
        }
        .control-btn:hover { background: #eef1f4; }
        .folder-row > td { background: #f6f8fa; font-weight: 600; cursor: pointer; user-select: none; }
        .folder-cell { display: flex; align-items: center; gap: 8px; }
        .folder-toggle {
            appearance: none;
            border: 1px solid #d0d7de;
            background: #ffffff;
            color: #24292e;
            border-radius: 4px;
            width: 24px;
            height: 24px;
            line-height: 1;
            cursor: pointer;
        }
        .file-name { display: inline-block; padding-left: 32px; }
        """;

    private const string Script = """
    (() => {
        function getFolderRow(nodeId) {
            return document.querySelector(`tr.folder-row[data-node-id="${nodeId}"]`);
        }

        function getToggle(nodeId) {
            return document.querySelector(`button.folder-toggle[data-node-id="${nodeId}"]`);
        }

        function isNodeExpanded(nodeId) {
            const toggle = getToggle(nodeId);
            if (!toggle) {
                return true;
            }

            return toggle.getAttribute("aria-expanded") !== "false";
        }

        function setNodeExpanded(nodeId, expanded) {
            const toggle = getToggle(nodeId);
            if (!toggle) {
                return;
            }

            toggle.textContent = expanded ? "▾" : "▸";
            toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
        }

        function ancestorsExpanded(parentId) {
            let currentId = parentId;
            while (currentId) {
                if (!isNodeExpanded(currentId)) {
                    return false;
                }

                const row = getFolderRow(currentId);
                if (!row) {
                    break;
                }

                currentId = row.getAttribute("data-parent-id");
            }

            return true;
        }

        function refreshVisibility() {
            const childRows = document.querySelectorAll("tr[data-parent-id]");
            for (const row of childRows) {
                const parentId = row.getAttribute("data-parent-id");
                if (!parentId) {
                    row.style.display = "";
                    continue;
                }

                row.style.display = ancestorsExpanded(parentId) ? "" : "none";
            }
        }

        function setAll(expanded) {
            const toggles = document.querySelectorAll("button.folder-toggle[data-node-id]");
            for (const toggle of toggles) {
                const nodeId = toggle.getAttribute("data-node-id");
                if (nodeId) {
                    setNodeExpanded(nodeId, expanded);
                }
            }

            refreshVisibility();
        }

        document.addEventListener("click", event => {
            const target = event.target;
            if (!(target instanceof Element)) {
                return;
            }

            const folderRow = target.closest("tr.folder-row[data-node-id]");
            if (folderRow) {
                const nodeId = folderRow.getAttribute("data-node-id");
                if (!nodeId) {
                    return;
                }

                setNodeExpanded(nodeId, !isNodeExpanded(nodeId));
                refreshVisibility();
                return;
            }

            if (target.matches("button#collapse-all")) {
                setAll(false);
                return;
            }

            if (target.matches("button#expand-all")) {
                setAll(true);
            }
        });

        refreshVisibility();
    })();
    """;

    private static readonly IReadOnlyDictionary<string, bool> CommentSupportByLanguage =
                LanguageRegistry.Languages.ToDictionary(
            l => l.Name,
            l => l.ShowHealth && (l.LineCommentTokens.Count > 0 || l.BlockComments.Count > 0),
            StringComparer.OrdinalIgnoreCase);

    private readonly TextWriter _writer;

    /// <summary>
    /// Initializes a new instance of <see cref="HtmlRenderer"/>.
    /// </summary>
    /// <param name="writer">
    /// The destination writer. Defaults to <see cref="Console.Out"/> when <see langword="null"/>.
    /// </param>
    public HtmlRenderer(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Out;
    }

    /// <inheritdoc />
    public void Render(AnalysisSummary summary, bool byFile, bool noHealth)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new StringBuilder();
        BuildDocument(sb, summary, byFile, noHealth);
        _writer.Write(sb);
    }

    private static void BuildDocument(StringBuilder sb, AnalysisSummary summary, bool byFile, bool noHealth)
    {
        var htmlLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-Hans" : "en";
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang=\"{htmlLang}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>{Encode(CliResources.HtmlTitle)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine(Css);
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"<h1>{Encode(CliResources.HtmlTitle)}</h1>");
        sb.AppendLine($"<p class=\"meta\">{CliResources.HtmlGenerated} {Encode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))} &nbsp;|&nbsp; {summary.FileCount:N0} {CliResources.HtmlFilesUnit} &nbsp;|&nbsp; {summary.Total:N0} {CliResources.HtmlTotalLines}</p>");

        if (byFile)
        {
            BuildFileSection(sb, summary, noHealth);
        }
        else
        {
            BuildLanguageSection(sb, summary, noHealth);
        }

        sb.AppendLine("<script>");
        sb.AppendLine(Script);
        sb.AppendLine("</script>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }

    private static void BuildFileSection(StringBuilder sb, AnalysisSummary summary, bool noHealth)
    {
        sb.AppendLine($"<h2>{Encode(CliResources.HtmlByFile)}</h2>");
        sb.AppendLine("<div class=\"file-controls\">");
        sb.AppendLine($"  <button id=\"collapse-all\" class=\"control-btn\" type=\"button\">{Encode(CliResources.HtmlCollapseAll)}</button>");
        sb.AppendLine($"  <button id=\"expand-all\" class=\"control-btn\" type=\"button\">{Encode(CliResources.HtmlExpandAll)}</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<table id=\"by-file-table\">");
        var healthTh = noHealth ? string.Empty : $"<th>{Encode(CliResources.ColHealth)}</th>";
        sb.AppendLine($"  <thead><tr><th>{Encode(CliResources.ColFile)}</th><th>{Encode(CliResources.ColLanguage)}</th><th class=\"r\">{Encode(CliResources.ColCode)}</th><th class=\"r\">{Encode(CliResources.ColComment)}</th><th class=\"r\">{Encode(CliResources.ColBlank)}</th><th class=\"r\">{Encode(CliResources.ColTotal)}</th>{healthTh}</tr></thead>");
        sb.AppendLine("  <tbody>");

        var root = BuildFolderTree(summary.Files);
        ComputeTotals(root);
        root = SkipTrivialRoot(root);

        var folderIndex = 0;

        if (root.Name.Length > 0)
        {
            RenderFolderRecursive(sb, root, null, 0, ref folderIndex, noHealth);
        }
        else if (root.Files.Count > 0)
        {
            var rootId = $"folder-{folderIndex}";
            folderIndex++;

            RenderFolderRow(sb, rootId, null, CliResources.HtmlRoot, root, 0, noHealth);

            foreach (var file in root.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                RenderFileRow(sb, rootId, file, 1, noHealth);
            }

            foreach (var child in root.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                RenderFolderRecursive(sb, child, rootId, 1, ref folderIndex, noHealth);
            }
        }
        else
        {
            foreach (var child in root.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                RenderFolderRecursive(sb, child, null, 0, ref folderIndex, noHealth);
            }
        }

        sb.AppendLine("  </tbody>");
        sb.Append("  <tfoot><tr>");
        sb.Append($"<td>{Encode(CliResources.RowTotal)}</td>");
        sb.Append($"<td class=\"r\">{summary.FileCount:N0} {Encode(CliResources.HtmlFilesUnit)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Code, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Comment, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Blank, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{summary.Total:N0}</td>");
        if (noHealth)
        {
            sb.AppendLine("</tr></tfoot>");
        }
        else
        {
            sb.AppendLine("<td></td></tr></tfoot>");
        }
        sb.AppendLine("</table>");
    }

    private static FolderNode BuildFolderTree(IReadOnlyList<FileAnalysis> files)
    {
        var root = new FolderNode(string.Empty);

        foreach (var file in files)
        {
            var relativePath = NormalizePath(ToRelative(file.Path));
            var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (!current.Children.TryGetValue(segment, out var child))
                {
                    child = new FolderNode(segment);
                    current.Children[segment] = child;
                }

                current = child;
            }

            current.Files.Add(new FileEntry
            {
                File = file,
                Name = parts[^1]
            });
        }

        return root;
    }

    private static void BuildLanguageSection(StringBuilder sb, AnalysisSummary summary, bool noHealth)
    {
        sb.AppendLine($"<h2>{Encode(CliResources.HtmlByLanguage)}</h2>");
        sb.AppendLine("<table>");
        var healthTh = noHealth ? string.Empty : $"<th>{Encode(CliResources.ColHealth)}</th>";
        sb.AppendLine($"  <thead><tr><th>{Encode(CliResources.ColLanguage)}</th><th class=\"r\">{Encode(CliResources.ColFiles)}</th><th class=\"r\">{Encode(CliResources.ColCode)}</th><th class=\"r\">{Encode(CliResources.ColComment)}</th><th class=\"r\">{Encode(CliResources.ColBlank)}</th><th class=\"r\">{Encode(CliResources.ColTotal)}</th>{healthTh}</tr></thead>");
        sb.AppendLine("  <tbody>");

        foreach (var lang in summary.ByLanguage)
        {
            sb.Append("    <tr>");
            sb.Append($"<td>{Encode(lang.Language)}</td>");
            sb.Append($"<td class=\"r\">{lang.Files:N0}</td>");
            sb.Append($"<td class=\"r\">{NumCell(lang.Code, lang.Total, noHealth)}</td>");
            sb.Append($"<td class=\"r\">{NumCell(lang.Comment, lang.Total, noHealth)}</td>");
            sb.Append($"<td class=\"r\">{NumCell(lang.Blank, lang.Total, noHealth)}</td>");
            sb.Append($"<td class=\"r\">{lang.Total:N0}</td>");
            if (!noHealth)
            {
                sb.Append($"<td>{HealthCell(lang.Language, lang.Code, lang.Comment)}</td>");
            }
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("  </tbody>");
        sb.Append("  <tfoot><tr>");
        sb.Append($"<td>{Encode(CliResources.RowTotal)}</td>");
        sb.Append($"<td class=\"r\">{summary.FileCount:N0}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Code, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Comment, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(summary.Blank, summary.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{summary.Total:N0}</td>");
        if (noHealth)
        {
            sb.AppendLine("</tr></tfoot>");
        }
        else
        {
            sb.AppendLine("<td></td></tr></tfoot>");
        }
        sb.AppendLine("</table>");
    }

    private static void ComputeTotals(FolderNode node)
    {
        var code = 0;
        var comment = 0;
        var blank = 0;
        var fileCount = 0;

        var healthCode = 0;
        var healthComment = 0;
        var hasHealthSupport = false;

        foreach (var file in node.Files)
        {
            code += file.File.Code;
            comment += file.File.Comment;
            blank += file.File.Blank;
            fileCount++;

            if (CommentSupportByLanguage.TryGetValue(file.File.Language, out var supports) && supports)
            {
                healthCode += file.File.Code;
                healthComment += file.File.Comment;
                hasHealthSupport = true;
            }
        }

        foreach (var child in node.Children.Values)
        {
            ComputeTotals(child);

            code += child.Code;
            comment += child.Comment;
            blank += child.Blank;
            fileCount += child.FileCount;

            healthCode += child.HealthCode;
            healthComment += child.HealthComment;
            hasHealthSupport = hasHealthSupport || child.HasHealthSupport;
        }

        node.Code = code;
        node.Comment = comment;
        node.Blank = blank;
        node.FileCount = fileCount;
        node.HealthCode = healthCode;
        node.HealthComment = healthComment;
        node.HasHealthSupport = hasHealthSupport;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string HealthCell(string language, int code, int comment)
    {
        if (!(CommentSupportByLanguage.TryGetValue(language, out var supports) && supports))
        {
            return "<span class=\"h-na\">\u2014</span>";
        }

        var codeAndComment = code + comment;
        if (codeAndComment == 0)
        {
            return "<span class=\"h-na\">\u2014</span>";
        }

        var density = (double)comment / codeAndComment;
        var (cls, label) = density switch
        {
            <= 0.0 => ("h-none", CliResources.HealthNone),
            < 0.05 => ("h-low", CliResources.HealthLow),
            < 0.10 => ("h-fair", CliResources.HealthFair),
            < 0.25 => ("h-good", CliResources.HealthGood),
            < 0.40 => ("h-high", CliResources.HealthHigh),
            _ => ("h-dense", CliResources.HealthDense)
        };

        return $"<span class=\"{cls}\">{label}</span>";
    }

    private static string HealthCellAggregate(int code, int comment, bool supports)
    {
        if (!supports)
        {
            return "<span class=\"h-na\">—</span>";
        }

        var codeAndComment = code + comment;
        if (codeAndComment == 0)
        {
            return "<span class=\"h-na\">—</span>";
        }

        var density = (double)comment / codeAndComment;
        var (cls, label) = density switch
        {
            <= 0.0 => ("h-none", CliResources.HealthNone),
            < 0.05 => ("h-low", CliResources.HealthLow),
            < 0.10 => ("h-fair", CliResources.HealthFair),
            < 0.25 => ("h-good", CliResources.HealthGood),
            < 0.40 => ("h-high", CliResources.HealthHigh),
            _ => ("h-dense", CliResources.HealthDense)
        };

        return $"<span class=\"{cls}\">{label}</span>";
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string NumCell(int count, int total, bool noHealth = false)
    {
        if (noHealth || total == 0)
        {
            return count.ToString("N0");
        }

        var pct = (double)count / total * 100;
        return $"{count:N0}<span class=\"pct\">({pct:F0}%)</span>";
    }

    private static void RenderFileRow(StringBuilder sb, string parentId, FileEntry entry, int depth, bool noHealth)
    {
        var indent = 36 + depth * 18;

        sb.Append($"    <tr class=\"file-row\" data-parent-id=\"{parentId}\">");
        sb.Append($"<td style=\"padding-left:{indent}px;\">{Encode(entry.Name)}</td>");
        sb.Append($"<td>{Encode(entry.File.Language)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(entry.File.Code, entry.File.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(entry.File.Comment, entry.File.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(entry.File.Blank, entry.File.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{entry.File.Total:N0}</td>");
        if (!noHealth)
        {
            sb.Append($"<td>{HealthCell(entry.File.Language, entry.File.Code, entry.File.Comment)}</td>");
        }
        sb.AppendLine("</tr>");
    }

    private static void RenderFolderRecursive(StringBuilder sb, FolderNode node, string? parentId, int depth, ref int folderIndex, bool noHealth)
    {
        var nodeId = $"folder-{folderIndex}";
        folderIndex++;

        RenderFolderRow(sb, nodeId, parentId, node.Name, node, depth, noHealth);

        foreach (var file in node.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            RenderFileRow(sb, nodeId, file, depth + 1, noHealth);
        }

        foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            RenderFolderRecursive(sb, child, nodeId, depth + 1, ref folderIndex, noHealth);
        }
    }

    private static void RenderFolderRow(StringBuilder sb, string nodeId, string? parentId, string label, FolderNode node, int depth, bool noHealth)
    {
        var indent = 8 + depth * 18;
        var parentAttr = parentId is null ? string.Empty : $" data-parent-id=\"{parentId}\"";
        var expanded = depth == 0;
        var toggleIcon = expanded ? "▾" : "▸";

        sb.Append($"    <tr class=\"folder-row\" data-node-id=\"{nodeId}\"{parentAttr}>");
        sb.Append("<td>");
        sb.Append($"<div class=\"folder-cell\" style=\"padding-left:{indent}px;\">");
        sb.Append($"<button type=\"button\" class=\"folder-toggle\" data-node-id=\"{nodeId}\" aria-expanded=\"{(expanded ? "true" : "false")}\">{toggleIcon}</button>");
        sb.Append($"<span>{Encode(label)}</span>");
        sb.Append("</div>");
        sb.Append("</td>");
        sb.Append($"<td>{node.FileCount:N0} {CliResources.HtmlFilesUnit}</td>");
        sb.Append($"<td class=\"r\">{NumCell(node.Code, node.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(node.Comment, node.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{NumCell(node.Blank, node.Total, noHealth)}</td>");
        sb.Append($"<td class=\"r\">{node.Total:N0}</td>");
        if (!noHealth)
        {
            sb.Append($"<td>{HealthCellAggregate(node.HealthCode, node.HealthComment, node.HasHealthSupport)}</td>");
        }
        sb.AppendLine("</tr>");
    }

    /// <summary>
    /// Advances past any single-child, file-less chain at the root of the
    /// folder tree, removing path-prefix segments that would otherwise appear
    /// as an unnecessary extra collapsible level at the top of the file view.
    /// </summary>
    /// <param name="root">The root node to start from.</param>
    /// <returns>
    /// The effective root node after skipping trivial single-child ancestors.
    /// </returns>
    private static FolderNode SkipTrivialRoot(FolderNode root)
    {
        while (root.Files.Count == 0 && root.Children.Count == 1)
        {
            root = root.Children.Values.First();
        }

        return root;
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

    private sealed class FileEntry
    {
        public required FileAnalysis File
        {
            get;
            init;
        }

        public required string Name
        {
            get;
            init;
        }
    }

    private sealed class FolderNode(string name)
    {
        public int Blank
        {
            get;
            set;
        }

        public Dictionary<string, FolderNode> Children
        {
            get;
        } = new(StringComparer.OrdinalIgnoreCase);

        public int Code
        {
            get;
            set;
        }

        public int Comment
        {
            get;
            set;
        }

        public int FileCount
        {
            get;
            set;
        }

        public List<FileEntry> Files
        {
            get;
        } = [];

        public bool HasHealthSupport
        {
            get;
            set;
        }

        public int HealthCode
        {
            get;
            set;
        }

        public int HealthComment
        {
            get;
            set;
        }

        public string Name
        {
            get;
        } = name;

        public int Total => Code + Comment + Blank;
    }
}