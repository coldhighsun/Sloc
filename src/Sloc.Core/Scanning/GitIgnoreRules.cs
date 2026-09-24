using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Sloc.Core.Scanning;

/// <summary>
/// Evaluates whether a path is ignored according to a set of <c>.gitignore</c> files,
/// supporting nested ignore files, negation (<c>!</c>), directory-only patterns
/// (trailing <c>/</c>), anchoring (a leading or embedded <c>/</c>), and the <c>*</c>,
/// <c>?</c>, and <c>**</c> wildcards.
/// </summary>
/// <remarks>
/// This is a pragmatic implementation of the gitignore format. Character classes
/// (<c>[a-z]</c>) are matched approximately. Once an ancestor directory's own cumulative
/// decision is "ignored", no pattern at a deeper path (including a negation) can
/// re-include anything under it, matching git's rule that an excluded directory is never
/// scanned for re-inclusion patterns.
/// </remarks>
public sealed class GitIgnoreRules
{
    // Caches the cumulative ignored/not-ignored state through each ancestor directory, so
    // sibling files under the same directory only re-evaluate patterns for their own leaf
    // name instead of re-walking every ancestor directory's patterns each time. Not safe
    // for concurrent use; IsIgnored is only called from the scanner's sequential file walk.
    private readonly Dictionary<string, bool> _directoryIgnoreCache = new();

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
    /// <param name="symlinkedDirectories">
    /// Receives the full paths of directory symlinks/junctions encountered while walking
    /// the tree for <c>.gitignore</c> files, so callers do not need a second full-tree walk
    /// to find the same directories for their own loop protection.
    /// </param>
    /// <param name="followSymlinks">Whether to follow symbolic links when walking the directory tree.</param>
    /// <returns>The loaded rule set (possibly empty).</returns>
    public static GitIgnoreRules Load(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        Action<int, string>? onDirectoryVisited,
        out IReadOnlyList<string> symlinkedDirectories,
        bool followSymlinks = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        var walk = ScanTreeWalker.Walk(
            root, excludedDirectoryNames, recursive, followSymlinks,
            collectGitignore: true, collectGitattributes: false, collectFiles: false, onDirectoryVisited);

        symlinkedDirectories = walk.SymlinkedDirectories;
        return FromWalk(root, walk.GitignoreFiles);
    }

    /// <inheritdoc cref="Load(string, IReadOnlySet{string}, bool, Action{int, string}?, out IReadOnlyList{string}, bool)"/>
    public static GitIgnoreRules Load(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive = true,
        Action<int, string>? onDirectoryVisited = null) =>
        Load(root, excludedDirectoryNames, recursive, onDirectoryVisited, out _);

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

        // Evaluate each ancestor directory then the leaf, last match wins within a given
        // segment. But once an ancestor directory's own cumulative decision is "ignored",
        // git never descends into it to look for re-inclusion patterns, so no pattern at
        // any deeper segment (including a negation) can flip it back: the "ignored" state
        // is locked for every segment below that point. Ancestor directories' cumulative
        // state is cached, since many files typically share the same parent directories.
        for (var depth = 0; depth < segments.Length; depth++)
        {
            var isDirectory = depth < segments.Length - 1;
            var partial = string.Join('/', segments, 0, depth + 1);

            if (isDirectory && _directoryIgnoreCache.TryGetValue(partial, out var cached))
            {
                ignored = cached;
                continue;
            }

            if (ignored)
            {
                if (isDirectory)
                {
                    _directoryIgnoreCache[partial] = true;
                }

                continue;
            }

            var decision = Evaluate(partial, isDirectory);
            if (decision.HasValue)
            {
                ignored = decision.Value;
            }

            if (isDirectory)
            {
                _directoryIgnoreCache[partial] = ignored;
            }
        }

        return ignored;
    }

    internal static List<GitIgnorePattern> CompilePatterns(IEnumerable<string> lines)
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

    /// <summary>
    /// Builds a rule set from <c>.gitignore</c> files already discovered by a
    /// <see cref="ScanTreeWalker"/> pass, adding the user's global <c>core.excludesFile</c>
    /// and the repo-local <c>.git/info/exclude</c> (lowest precedence, evaluated first),
    /// without walking the directory tree again.
    /// </summary>
    internal static GitIgnoreRules FromWalk(string root, IReadOnlyList<GitIgnoreFile> walkedFiles)
    {
        var fullRoot = Path.GetFullPath(root);

        // Lowest precedence first: the user's global excludesFile, then the repo-local
        // .git/info/exclude, then the per-directory .gitignore files discovered below
        // (root .gitignore, then nested ones). All of these share BaseDirectory "", so a
        // stable sort (not List<T>.Sort, which isn't stable) is required to preserve this
        // relative order.
        var files = new List<GitIgnoreFile>();
        AddIfPresent(files, LoadGlobalExcludesFile(fullRoot));
        AddIfPresent(files, LoadRepoExcludeFile(fullRoot));
        files.AddRange(walkedFiles);

        return new GitIgnoreRules(files.OrderBy(file => file.BaseDirectory.Length).ToList());
    }

    internal static string NormalizeBase(string baseDirectory)
    {
        var normalized = baseDirectory.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    /// <summary>
    /// Parses the <c>[core] excludesFile</c> setting out of raw <c>.gitconfig</c> contents.
    /// A minimal, single-file parser: does not resolve <c>include</c> directives or other
    /// config sources git would also consult.
    /// </summary>
    /// <param name="gitConfigContents">The raw contents of a git config file.</param>
    /// <param name="excludesFilePath">
    /// The resolved path (with a leading <c>~</c> expanded to the user's home directory),
    /// if the setting was found.
    /// </param>
    /// <returns><see langword="true"/> if an <c>excludesFile</c> setting was found.</returns>
    internal static bool TryParseExcludesFile(
        string gitConfigContents,
        [NotNullWhen(true)] out string? excludesFilePath) =>
        TryParseExcludesFile(gitConfigContents, UserHomeDirectory, out excludesFilePath);

    /// <inheritdoc cref="TryParseExcludesFile(string, out string?)"/>
    /// <param name="gitConfigContents">The raw contents of a git config file.</param>
    /// <param name="homeDirectory">The directory a leading <c>~</c> expands to.</param>
    /// <param name="excludesFilePath">
    /// The resolved path (with a leading <c>~</c> expanded to <paramref name="homeDirectory"/>),
    /// if the setting was found. When it is set more than once, the last value wins, as in git.
    /// </param>
    internal static bool TryParseExcludesFile(
        string gitConfigContents,
        string homeDirectory,
        [NotNullWhen(true)] out string? excludesFilePath)
    {
        excludesFilePath = null;
        var inCoreSection = false;

        foreach (var rawLine in gitConfigContents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                // Only the plain [core] section; [core "name"] is a different (sub)section.
                var close = line.IndexOf(']');
                inCoreSection = close > 0 && line[1..close].Trim().Equals("core", StringComparison.OrdinalIgnoreCase);

                // A setting may follow the section header on the same line.
                line = close < 0 ? string.Empty : line[(close + 1)..].TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }
            }

            if (!inCoreSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!key.Equals("excludesFile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = ParseConfigValue(line[(separator + 1)..]);
            if (value.Length == 0)
            {
                continue;
            }

            excludesFilePath = value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal)
                ? Path.Combine(homeDirectory, value[1..].TrimStart('/', '\\'))
                : value;
        }

        return excludesFilePath is not null;
    }

    /// <summary>
    /// Resolves the global ignore file git would use: <c>core.excludesFile</c> from the
    /// config files git reads, in its order, the last one that sets it winning:
    /// <c>$XDG_CONFIG_HOME/git/config</c> (default <c>~/.config/git/config</c>) and
    /// <c>~/.gitconfig</c> (both replaced by <c>$GIT_CONFIG_GLOBAL</c> when that is set),
    /// then the repository's own <c>.git/config</c>. When none sets it, git's default of
    /// <c>$XDG_CONFIG_HOME/git/ignore</c> (default <c>~/.config/git/ignore</c>).
    /// </summary>
    /// <param name="homeDirectory">The user's home directory, as git resolves it (<c>$HOME</c>).</param>
    /// <param name="xdgConfigHome">The value of <c>XDG_CONFIG_HOME</c>, if set.</param>
    /// <param name="gitConfigGlobal">The value of <c>GIT_CONFIG_GLOBAL</c>, if set.</param>
    /// <param name="repoConfigPath">The repository's <c>.git/config</c> path, if any.</param>
    /// <returns>The path of the global ignore file, which may not exist.</returns>
    internal static string ResolveGlobalExcludesFilePath(
        string homeDirectory,
        string? xdgConfigHome,
        string? gitConfigGlobal = null,
        string? repoConfigPath = null)
    {
        var xdgGitDirectory = string.IsNullOrEmpty(xdgConfigHome)
            ? Path.Combine(homeDirectory, ".config", "git")
            : Path.Combine(xdgConfigHome, "git");

        List<string> configPaths = string.IsNullOrEmpty(gitConfigGlobal)
            ? [Path.Combine(xdgGitDirectory, "config"), Path.Combine(homeDirectory, ".gitconfig")]
            : [gitConfigGlobal];
        if (repoConfigPath is not null)
        {
            configPaths.Add(repoConfigPath);
        }

        string? excludesFilePath = null;
        foreach (var configPath in configPaths)
        {
            string contents;
            try
            {
                contents = File.ReadAllText(configPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (TryParseExcludesFile(contents, homeDirectory, out var configured))
            {
                excludesFilePath = configured;
            }
        }

        return excludesFilePath ?? Path.Combine(xdgGitDirectory, "ignore");
    }

    /// <summary>
    /// The home directory git uses: <c>$HOME</c> when set (Git for Windows honors it too),
    /// otherwise the user profile directory.
    /// </summary>
    private static string UserHomeDirectory =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Decodes a git config value: double quotes are removed (whitespace, <c>#</c>, and
    /// <c>;</c> inside them are kept), an unquoted <c>#</c> or <c>;</c> starts a comment,
    /// unquoted leading/trailing whitespace is dropped, and <c>\"</c> and <c>\\</c> are
    /// unescaped. <c>\n</c>, <c>\t</c>, <c>\b</c> are unescaped only inside quotes; outside
    /// them, like any other backslash (which git would reject), they are kept as-is, so an
    /// unquoted Windows path like <c>C:\tools\new\.gitignore</c> still works.
    /// </summary>
    private static string ParseConfigValue(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var inQuotes = false;
        var keepLength = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                keepLength = sb.Length;
                continue;
            }

            if (!inQuotes && c is '#' or ';')
            {
                break;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[i + 1];
                var unescaped = next switch
                {
                    '"' or '\\' => next,
                    'n' when inQuotes => '\n',
                    't' when inQuotes => '\t',
                    'b' when inQuotes => '\b',
                    _ => (char?)null
                };

                if (unescaped is { } u)
                {
                    sb.Append(u);
                    keepLength = sb.Length;
                    i++;
                    continue;
                }
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    sb.Append(c);
                }

                continue;
            }

            sb.Append(c);
            keepLength = sb.Length;
        }

        return sb.ToString(0, keepLength);
    }

    private static void AddIfPresent(List<GitIgnoreFile> files, GitIgnoreFile? file)
    {
        if (file is not null)
        {
            files.Add(file);
        }
    }

    /// <summary>
    /// Loads the user's global ignore file (see <see cref="ResolveGlobalExcludesFilePath"/>),
    /// if it exists. Like <see cref="LoadRepoExcludeFile"/>, only a <c>.git/config</c> at
    /// the scan root is consulted.
    /// </summary>
    private static GitIgnoreFile? LoadGlobalExcludesFile(string scanRoot) =>
        TryLoadIgnoreFile(ResolveGlobalExcludesFilePath(
            UserHomeDirectory,
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"),
            Path.Combine(scanRoot, ".git", "config")));

    /// <summary>
    /// Loads the repo-local <c>.git/info/exclude</c> file at the scan root, if present.
    /// Does not search upward for a repository boundary, matching how local
    /// <c>.gitignore</c> discovery only looks at the scan root down.
    /// </summary>
    private static GitIgnoreFile? LoadRepoExcludeFile(string scanRoot)
    {
        var excludePath = Path.Combine(scanRoot, ".git", "info", "exclude");
        return TryLoadIgnoreFile(excludePath);
    }

    private static GitIgnoreFile? TryLoadIgnoreFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var patterns = CompilePatterns(lines);
        return patterns.Count == 0 ? null : new GitIgnoreFile(string.Empty, patterns);
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

    internal sealed class GitIgnoreFile(string baseDirectory, IReadOnlyList<GitIgnorePattern> patterns)
    {
        public string BaseDirectory { get; } = baseDirectory;

        public IReadOnlyList<GitIgnorePattern> Patterns { get; } = patterns;

        public bool TryGetRelativePath(string path, out string relativeToBase) =>
            RelativePathResolver.TryGetRelativePath(BaseDirectory, path, out relativeToBase);
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

        if (!TryCompileBody(line, out var regex, out var directoryOnly))
        {
            return false;
        }

        pattern = new GitIgnorePattern(regex, isNegation, directoryOnly);
        return true;
    }

    /// <summary>
    /// Compiles a single gitignore-style glob pattern (no leading <c>!</c> negation, since
    /// that syntax is specific to ignore files) to a regex. Shared with
    /// <see cref="GitAttributesRules"/>, whose <c>.gitattributes</c> pattern column uses the
    /// same glob dialect (anchoring, <c>**</c>, character classes, trailing-<c>/</c>
    /// directory-only).
    /// </summary>
    internal static bool TryCompilePattern(string rawPattern, out Regex regex, out bool directoryOnly) =>
        TryCompileBody(rawPattern, out regex, out directoryOnly);

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
                    var negate = inner.StartsWith('!');
                    sb.Append('[');
                    if (negate)
                    {
                        sb.Append('^');
                        inner = inner[1..];
                    }

                    // Escape backslashes and literal carets: gitignore character classes
                    // have no regex escape sequences (\d, \b, etc. are just two literal
                    // characters) and no negation marker other than a leading "!" (handled
                    // above), so an un-escaped "\" or "^" here would change the regex's
                    // meaning instead of matching the literal character.
                    foreach (var ch in inner)
                    {
                        if (ch is '\\' or '^')
                        {
                            sb.Append('\\');
                        }

                        sb.Append(ch);
                    }

                    sb.Append(']');
                    i = end + 1;
                }
            }
            else
            {
                // Batch consecutive literal characters into a single Regex.Escape call
                // instead of escaping (and allocating) one character at a time.
                var start = i;
                do
                {
                    i++;
                }
                while (i < pattern.Length && pattern[i] is not ('*' or '?' or '['));

                sb.Append(Regex.Escape(pattern[start..i]));
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

    private static bool TryCompileBody(string line, out Regex regex, out bool directoryOnly)
    {
        regex = null!;
        directoryOnly = false;

        if (line.Length == 0)
        {
            return false;
        }

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
        regex = new Regex(
            prefix + body + "$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return true;
    }
}