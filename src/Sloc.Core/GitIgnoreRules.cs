using System.Text;
using System.Text.RegularExpressions;

namespace Sloc.Core;

/// <summary>
/// Evaluates whether a path is ignored according to a set of <c>.gitignore</c> files,
/// supporting nested ignore files, negation (<c>!</c>), directory-only patterns
/// (trailing <c>/</c>), anchoring (a leading or embedded <c>/</c>), and the <c>*</c>,
/// <c>?</c>, and <c>**</c> wildcards.
/// </summary>
/// <remarks>
/// This is a pragmatic implementation of the gitignore format. Character classes
/// (<c>[a-z]</c>) are matched approximately, and the git optimization that a file under
/// an already-ignored directory cannot be re-included is applied via last-match-wins
/// across the path's ancestors.
/// </remarks>
public sealed class GitIgnoreRules
{
    private readonly IReadOnlyList<GitIgnoreFile> _files;

    private GitIgnoreRules(IReadOnlyList<GitIgnoreFile> files)
    {
        _files = files;
    }

    /// <summary>
    /// Gets a value indicating whether any patterns were loaded.
    /// </summary>
    public bool IsEmpty => _files.Count == 0;

    /// <summary>
    /// Parses the supplied ignore-file lines as a single <c>.gitignore</c> rooted at
    /// <paramref name="baseDirectory"/> (relative to the scan root, <c>/</c>-separated).
    /// Intended for tests.
    /// </summary>
    /// <param name="baseDirectory">The base directory the patterns are relative to.</param>
    /// <param name="lines">The raw ignore-file lines.</param>
    /// <returns>The loaded rule set.</returns>
    public static GitIgnoreRules FromLines(string baseDirectory, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(lines);

        var patterns = CompilePatterns(lines);
        return new GitIgnoreRules(patterns.Count == 0
            ? []
            : [new GitIgnoreFile(NormalizeBase(baseDirectory), patterns)]);
    }

    /// <summary>
    /// Discovers and loads every <c>.gitignore</c> file under <paramref name="root"/>,
    /// skipping the supplied excluded directory names.
    /// </summary>
    /// <param name="root">The scan root directory.</param>
    /// <param name="excludedDirectoryNames">
    /// Directory names not to descend into when searching for ignore files (e.g. <c>.git</c>).
    /// </param>
    /// <param name="recursive">
    /// Whether to search subdirectories for nested <c>.gitignore</c> files. When
    /// <see langword="false"/>, only the <paramref name="root"/> directory itself is checked,
    /// matching the shallow file discovery performed by the scanner.
    /// </param>
    /// <param name="onDirectoryVisited">
    /// An optional callback invoked each time a directory is visited while searching for
    /// <c>.gitignore</c> files, receiving the running count and the directory's full path.
    /// Used to report progress on long-running scans; has no effect on the result.
    /// </param>
    /// <returns>The loaded rule set (possibly empty).</returns>
    public static GitIgnoreRules Load(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive = true,
        Action<int, string>? onDirectoryVisited = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        var files = new List<GitIgnoreFile>();
        var fullRoot = Path.GetFullPath(root);
        var visited = 0;
        CollectGitIgnoreFiles(fullRoot, fullRoot, excludedDirectoryNames, recursive, files, onDirectoryVisited, ref visited);

        // Shallower ignore files first, so deeper ones take precedence (evaluated later).
        files.Sort((left, right) => left.BaseDirectory.Length.CompareTo(right.BaseDirectory.Length));
        return new GitIgnoreRules(files);
    }

    /// <summary>
    /// Determines whether the given path (relative to the scan root, using either
    /// separator) is ignored.
    /// </summary>
    /// <param name="relativePath">The path relative to the scan root.</param>
    /// <returns><see langword="true"/> if the path is ignored.</returns>
    public bool IsIgnored(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (_files.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/');
        var ignored = false;

        // Evaluate each ancestor directory then the leaf, last match wins. Once a
        // directory is ignored, its descendants stay ignored unless a later pattern
        // explicitly re-includes them.
        for (var depth = 0; depth < segments.Length; depth++)
        {
            var isDirectory = depth < segments.Length - 1;
            var partial = string.Join('/', segments, 0, depth + 1);
            var decision = Evaluate(partial, isDirectory);
            if (decision.HasValue)
            {
                ignored = decision.Value;
            }
        }

        return ignored;
    }

    private static void CollectGitIgnoreFiles(
        string directory,
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        List<GitIgnoreFile> files,
        Action<int, string>? onDirectoryVisited,
        ref int visited)
    {
        visited++;
        onDirectoryVisited?.Invoke(visited, directory);

        string[] entries;
        try
        {
            var ignorePath = Path.Combine(directory, ".gitignore");
            if (File.Exists(ignorePath))
            {
                var patterns = CompilePatterns(File.ReadAllLines(ignorePath));
                if (patterns.Count > 0)
                {
                    var baseDir = NormalizeBase(Path.GetRelativePath(root, directory));
                    files.Add(new GitIgnoreFile(baseDir, patterns));
                }
            }

            entries = recursive ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var subdirectory in entries)
        {
            var name = Path.GetFileName(subdirectory);
            if (excludedDirectoryNames.Contains(name))
            {
                continue;
            }

            CollectGitIgnoreFiles(subdirectory, root, excludedDirectoryNames, recursive, files, onDirectoryVisited, ref visited);
        }
    }

    private static List<GitIgnorePattern> CompilePatterns(IEnumerable<string> lines)
    {
        var patterns = new List<GitIgnorePattern>();
        foreach (var line in lines)
        {
            if (GitIgnorePattern.TryCompile(line, out var pattern))
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }

    private static string NormalizeBase(string baseDirectory)
    {
        var normalized = baseDirectory.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private bool? Evaluate(string path, bool isDirectory)
    {
        bool? result = null;
        foreach (var file in _files)
        {
            if (!file.TryGetRelativePath(path, out var relativeToBase))
            {
                continue;
            }

            foreach (var pattern in file.Patterns)
            {
                if (pattern.DirectoryOnly && !isDirectory)
                {
                    continue;
                }

                if (pattern.Regex.IsMatch(relativeToBase))
                {
                    result = !pattern.IsNegation;
                }
            }
        }

        return result;
    }

    private sealed class GitIgnoreFile(string baseDirectory, IReadOnlyList<GitIgnorePattern> patterns)
    {
        public string BaseDirectory { get; } = baseDirectory;

        public IReadOnlyList<GitIgnorePattern> Patterns { get; } = patterns;

        public bool TryGetRelativePath(string path, out string relativeToBase)
        {
            if (BaseDirectory.Length == 0)
            {
                relativeToBase = path;
                return true;
            }

            if (path.Length > BaseDirectory.Length
                && path.StartsWith(BaseDirectory, StringComparison.Ordinal)
                && path[BaseDirectory.Length] == '/')
            {
                relativeToBase = path[(BaseDirectory.Length + 1)..];
                return true;
            }

            relativeToBase = string.Empty;
            return false;
        }
    }
}

/// <summary>
/// A single compiled <c>.gitignore</c> pattern.
/// </summary>
internal sealed class GitIgnorePattern
{
    private GitIgnorePattern(Regex regex, bool isNegation, bool directoryOnly)
    {
        Regex = regex;
        IsNegation = isNegation;
        DirectoryOnly = directoryOnly;
    }

    public bool DirectoryOnly
    {
        get;
    }

    public bool IsNegation
    {
        get;
    }

    public Regex Regex
    {
        get;
    }

    public static bool TryCompile(string rawLine, out GitIgnorePattern pattern)
    {
        pattern = null!;

        var line = TrimTrailingUnescapedWhitespace(rawLine);
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }

        var isNegation = false;
        if (line[0] == '!')
        {
            isNegation = true;
            line = line[1..];
        }
        else if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return false;
        }

        var directoryOnly = false;
        if (line.EndsWith('/'))
        {
            directoryOnly = true;
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return false;
        }

        // A pattern is anchored to the base directory if it contains a slash (a trailing
        // slash was already removed above); a leading slash also anchors and is stripped.
        var anchored = line.Contains('/');
        if (line[0] == '/')
        {
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return false;
        }

        var body = Translate(line);
        var prefix = anchored ? "^" : "(?:^|.*/)";
        var regex = new Regex(prefix + body + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        pattern = new GitIgnorePattern(regex, isNegation, directoryOnly);
        return true;
    }

    private static string Translate(string pattern)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    var slashBefore = i == 0 || pattern[i - 1] == '/';
                    var slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    if (slashBefore && slashAfter)
                    {
                        sb.Append("(?:.*/)?");
                        i += 3;
                        continue;
                    }

                    if (slashBefore && i + 2 == pattern.Length)
                    {
                        sb.Append(".*");
                        i += 2;
                        continue;
                    }

                    sb.Append("[^/]*");
                    i += 2;
                    continue;
                }

                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0)
                {
                    sb.Append("\\[");
                    i++;
                }
                else
                {
                    var inner = pattern[(i + 1)..end];
                    sb.Append('[');
                    sb.Append(inner.StartsWith('!') ? "^" + inner[1..] : inner);
                    sb.Append(']');
                    i = end + 1;
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        return sb.ToString();
    }

    private static string TrimTrailingUnescapedWhitespace(string line)
    {
        var end = line.Length;
        while (end > 0 && char.IsWhiteSpace(line[end - 1]))
        {
            // A backslash immediately before the whitespace escapes it.
            if (end >= 2 && line[end - 2] == '\\')
            {
                break;
            }

            end--;
        }

        return line[..end];
    }
}