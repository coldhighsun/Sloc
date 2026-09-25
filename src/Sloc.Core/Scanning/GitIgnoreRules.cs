using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
/// (<c>[a-z]</c>, negated with <c>!</c> or <c>^</c>) and backslash escapes are supported;
/// POSIX bracket expressions (<c>[[:alpha:]]</c>) are not. Once an ancestor directory's own
/// cumulative decision is "ignored", no pattern at a deeper path (including a negation) can
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
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    /// <returns>The loaded rule set.</returns>
    public static GitIgnoreRules FromLines(string baseDirectory, IEnumerable<string> lines, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(lines);

        var patterns = CompilePatterns(lines, ignoreCase);
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

        var ignoreCase = ResolveIgnoreCase(root);
        var walk = ScanTreeWalker.Walk(
            root, excludedDirectoryNames, recursive, followSymlinks, ignoreCase,
            collectGitignore: true, collectGitattributes: false, collectFiles: false, onDirectoryVisited);

        symlinkedDirectories = walk.SymlinkedDirectories;
        return FromWalk(root, walk.GitignoreFiles, ignoreCase);
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

    /// <summary>
    /// Compiles the patterns of one ignore file, skipping comments, blank lines, and lines
    /// git never matches.
    /// </summary>
    /// <param name="lines">The raw ignore-file lines.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    internal static List<GitIgnorePattern> CompilePatterns(IEnumerable<string> lines, bool ignoreCase)
    {
        var patterns = new List<GitIgnorePattern>();
        foreach (var line in lines)
        {
            if (GitIgnorePattern.TryCompile(line, ignoreCase, out var pattern))
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
    /// <param name="root">The scan root directory.</param>
    /// <param name="walkedFiles">The <c>.gitignore</c> files found under <paramref name="root"/>.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    internal static GitIgnoreRules FromWalk(string root, IReadOnlyList<GitIgnoreFile> walkedFiles, bool ignoreCase)
    {
        var fullRoot = Path.GetFullPath(root);

        // Lowest precedence first: the user's global excludesFile, then the repo-local
        // .git/info/exclude, then the per-directory .gitignore files discovered below
        // (root .gitignore, then nested ones). All of these share BaseDirectory "", so a
        // stable sort (not List<T>.Sort, which isn't stable) is required to preserve this
        // relative order.
        var files = new List<GitIgnoreFile>();
        AddIfPresent(files, LoadGlobalExcludesFile(fullRoot, ignoreCase));
        AddIfPresent(files, LoadRepoExcludeFile(fullRoot, ignoreCase));
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
        foreach (var entry in ParseConfigEntries(gitConfigContents))
        {
            if (!IsCoreKey(entry, "excludesFile") || entry.Value is not { Length: > 0 } value)
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
        string? excludesFilePath = null;
        foreach (var contents in ReadConfigFiles(homeDirectory, xdgConfigHome, gitConfigGlobal, repoConfigPath))
        {
            if (TryParseExcludesFile(contents, homeDirectory, out var configured))
            {
                excludesFilePath = configured;
            }
        }

        return excludesFilePath ?? Path.Combine(XdgGitDirectory(homeDirectory, xdgConfigHome), "ignore");
    }

    /// <summary>
    /// Parses the <c>[core] ignoreCase</c> setting out of raw git config contents. When it is
    /// set more than once, the last value wins, as in git. A key with no <c>=</c> means
    /// <see langword="true"/>; a value git would reject as a boolean is skipped.
    /// </summary>
    /// <param name="gitConfigContents">The raw contents of a git config file.</param>
    /// <param name="ignoreCase">The configured value, if the setting was found.</param>
    /// <returns><see langword="true"/> if a valid <c>ignoreCase</c> setting was found.</returns>
    internal static bool TryParseIgnoreCase(string gitConfigContents, out bool ignoreCase)
    {
        ignoreCase = false;
        var found = false;
        foreach (var entry in ParseConfigEntries(gitConfigContents))
        {
            if (IsCoreKey(entry, "ignoreCase") && TryParseConfigBool(entry.Value, out var value))
            {
                ignoreCase = value;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Resolves whether ignore and attribute patterns match case-insensitively, as git's
    /// <c>core.ignoreCase</c> decides: the value from the global config files (in the order
    /// <see cref="ResolveGlobalExcludesFilePath"/> reads them) and then the repository's own
    /// config, the last one that sets it winning. When nothing sets it, the platform's usual
    /// file-system behavior: case-insensitive on Windows and macOS (where <c>git init</c> and
    /// <c>git clone</c> write <c>ignorecase = true</c>), case-sensitive elsewhere.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory, as git resolves it (<c>$HOME</c>).</param>
    /// <param name="xdgConfigHome">The value of <c>XDG_CONFIG_HOME</c>, if set.</param>
    /// <param name="gitConfigGlobal">The value of <c>GIT_CONFIG_GLOBAL</c>, if set.</param>
    /// <param name="repoConfigPath">The repository's config file path, if any.</param>
    /// <returns><see langword="true"/> if patterns should match case-insensitively.</returns>
    internal static bool ResolveIgnoreCase(
        string homeDirectory,
        string? xdgConfigHome,
        string? gitConfigGlobal,
        string? repoConfigPath)
    {
        var ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        foreach (var contents in ReadConfigFiles(homeDirectory, xdgConfigHome, gitConfigGlobal, repoConfigPath))
        {
            if (TryParseIgnoreCase(contents, out var configured))
            {
                ignoreCase = configured;
            }
        }

        return ignoreCase;
    }

    /// <summary>
    /// Resolves <c>core.ignoreCase</c> for a scan rooted at <paramref name="scanRoot"/> from
    /// the current user's global config and the <c>.git/config</c> at the scan root (see
    /// <see cref="ResolveIgnoreCase(string, string?, string?, string?)"/>).
    /// </summary>
    /// <param name="scanRoot">The scan root directory.</param>
    /// <returns><see langword="true"/> if patterns should match case-insensitively.</returns>
    internal static bool ResolveIgnoreCase(string scanRoot) =>
        ResolveIgnoreCase(
            UserHomeDirectory,
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"),
            Path.Combine(Path.GetFullPath(scanRoot), ".git", "config"));

    /// <summary>
    /// Splits raw git config contents into its settings, in file order. A minimal,
    /// single-file parser: does not resolve <c>include</c> directives or line continuations.
    /// Section names are lower-cased; a <c>[section "sub"]</c> header yields the subsection
    /// separately (case preserved), and a key with no <c>=</c> has a <see langword="null"/> value.
    /// </summary>
    /// <param name="contents">The raw contents of a git config file.</param>
    /// <returns>Each setting, with its decoded value (see <see cref="ParseConfigValue"/>).</returns>
    internal static IEnumerable<ConfigEntry> ParseConfigEntries(string contents)
    {
        var section = string.Empty;
        string? subsection = null;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                (section, subsection) = close > 0 ? ParseSectionHeader(line[1..close]) : (string.Empty, null);

                // A setting may follow the section header on the same line.
                line = close < 0 ? string.Empty : line[(close + 1)..].TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }
            }

            var separator = line.IndexOf('=');
            var key = (separator < 0 ? line : line[..separator]).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            yield return new ConfigEntry(
                section, subsection, key, separator < 0 ? null : ParseConfigValue(line[(separator + 1)..]));
        }
    }

    /// <summary>
    /// Reads git's boolean config syntax: <see langword="null"/> (a bare key), <c>true</c>,
    /// <c>yes</c>, <c>on</c>, or a nonzero integer are true; an empty value, <c>false</c>,
    /// <c>no</c>, <c>off</c>, or <c>0</c> are false (all case-insensitive).
    /// </summary>
    /// <param name="value">The decoded config value.</param>
    /// <param name="result">The parsed boolean.</param>
    /// <returns><see langword="false"/> if git would reject the value as a boolean.</returns>
    internal static bool TryParseConfigBool(string? value, out bool result)
    {
        switch (value?.ToLowerInvariant())
        {
            case null or "true" or "yes" or "on":
                result = true;
                return true;
            case "" or "false" or "no" or "off":
                result = false;
                return true;
        }

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            result = number != 0;
            return true;
        }

        result = false;
        return false;
    }

    /// <summary>
    /// Splits the text between a config section header's brackets into the lower-cased
    /// section name and the (case-preserved, unquoted) subsection, if any.
    /// </summary>
    /// <param name="header">The header text, without the surrounding brackets.</param>
    private static (string Section, string? Subsection) ParseSectionHeader(string header)
    {
        var quote = header.IndexOf('"');
        if (quote < 0)
        {
            return (header.Trim().ToLowerInvariant(), null);
        }

        var subsection = new StringBuilder();
        for (var i = quote + 1; i < header.Length && header[i] != '"'; i++)
        {
            if (header[i] == '\\' && i + 1 < header.Length)
            {
                i++;
            }

            subsection.Append(header[i]);
        }

        return (header[..quote].Trim().ToLowerInvariant(), subsection.ToString());
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is the plain <c>[core]</c> section's
    /// <paramref name="key"/> (case-insensitive; <c>[core "name"]</c> is a different subsection).
    /// </summary>
    /// <param name="entry">The parsed config setting.</param>
    /// <param name="key">The key name to look for.</param>
    private static bool IsCoreKey(ConfigEntry entry, string key) =>
        entry.Section == "core"
        && entry.Subsection is null
        && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the contents of the git config files <see cref="ResolveGlobalExcludesFilePath"/>
    /// documents, in git's order, skipping any that do not exist or cannot be read.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory, as git resolves it (<c>$HOME</c>).</param>
    /// <param name="xdgConfigHome">The value of <c>XDG_CONFIG_HOME</c>, if set.</param>
    /// <param name="gitConfigGlobal">The value of <c>GIT_CONFIG_GLOBAL</c>, if set.</param>
    /// <param name="repoConfigPath">The repository's config file path, if any.</param>
    private static IEnumerable<string> ReadConfigFiles(
        string homeDirectory,
        string? xdgConfigHome,
        string? gitConfigGlobal,
        string? repoConfigPath)
    {
        List<string> configPaths = string.IsNullOrEmpty(gitConfigGlobal)
            ? [Path.Combine(XdgGitDirectory(homeDirectory, xdgConfigHome), "config"), Path.Combine(homeDirectory, ".gitconfig")]
            : [gitConfigGlobal];
        if (repoConfigPath is not null)
        {
            configPaths.Add(repoConfigPath);
        }

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

            yield return contents;
        }
    }

    /// <summary>
    /// The directory git reads its XDG config and ignore files from:
    /// <c>$XDG_CONFIG_HOME/git</c>, defaulting to <c>~/.config/git</c>.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="xdgConfigHome">The value of <c>XDG_CONFIG_HOME</c>, if set.</param>
    private static string XdgGitDirectory(string homeDirectory, string? xdgConfigHome) =>
        string.IsNullOrEmpty(xdgConfigHome)
            ? Path.Combine(homeDirectory, ".config", "git")
            : Path.Combine(xdgConfigHome, "git");

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
    private static GitIgnoreFile? LoadGlobalExcludesFile(string scanRoot, bool ignoreCase) =>
        TryLoadIgnoreFile(
            ResolveGlobalExcludesFilePath(
                UserHomeDirectory,
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"),
                Path.Combine(scanRoot, ".git", "config")),
            ignoreCase);

    /// <summary>
    /// Loads the repo-local <c>.git/info/exclude</c> file at the scan root, if present.
    /// Does not search upward for a repository boundary, matching how local
    /// <c>.gitignore</c> discovery only looks at the scan root down.
    /// </summary>
    private static GitIgnoreFile? LoadRepoExcludeFile(string scanRoot, bool ignoreCase)
    {
        var excludePath = Path.Combine(scanRoot, ".git", "info", "exclude");
        return TryLoadIgnoreFile(excludePath, ignoreCase);
    }

    private static GitIgnoreFile? TryLoadIgnoreFile(string path, bool ignoreCase)
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

        var patterns = CompilePatterns(lines, ignoreCase);
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

    /// <summary>
    /// One setting read from a git config file by <see cref="ParseConfigEntries"/>.
    /// </summary>
    /// <param name="Section">The lower-cased section name (e.g. <c>core</c>).</param>
    /// <param name="Subsection">The subsection of a <c>[section "sub"]</c> header, if any.</param>
    /// <param name="Key">The setting's key, as written.</param>
    /// <param name="Value">The decoded value, or <see langword="null"/> for a key with no <c>=</c>.</param>
    internal readonly record struct ConfigEntry(string Section, string? Subsection, string Key, string? Value);

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

    public static bool TryCompile(string rawLine, bool ignoreCase, out GitIgnorePattern pattern)
    {
        pattern = null!;

        var line = TrimTrailingSpaces(rawLine);
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

        if (!TryCompileBody(line, ignoreCase, out var regex, out var directoryOnly))
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
    internal static bool TryCompilePattern(string rawPattern, bool ignoreCase, out Regex regex, out bool directoryOnly) =>
        TryCompileBody(rawPattern, ignoreCase, out regex, out directoryOnly);

    /// <summary>
    /// Translates a gitignore glob to a regex body, or returns <see langword="null"/> for a
    /// pattern git never matches (one ending in an unescaped backslash, or with an
    /// unterminated character class).
    /// </summary>
    private static string? Translate(string pattern)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                // A backslash makes the next character literal (e.g. "\*", "\ ", "\#").
                if (i + 1 == pattern.Length)
                {
                    return null;
                }

                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i += 2;
            }
            else if (c == '*')
            {
                // Like git's wildmatch, a run of two or more stars ("**", "***", ...) is one
                // "**" token; a single star never crosses a "/".
                var end = i + 1;
                while (end < pattern.Length && pattern[end] == '*')
                {
                    end++;
                }

                if (end - i >= 2)
                {
                    var slashBefore = i == 0 || pattern[i - 1] == '/';
                    var slashAfter = end < pattern.Length && pattern[end] == '/';
                    if (slashBefore && slashAfter)
                    {
                        sb.Append("(?:.*/)?");
                        i = end + 1;
                        continue;
                    }

                    if (slashBefore && end == pattern.Length)
                    {
                        sb.Append(".*");
                        i = end;
                        continue;
                    }
                }

                sb.Append("[^/]*");
                i = end;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                // No closing "]": git's wildmatch aborts, so the pattern matches nothing.
                if (!TryTranslateClass(pattern, i, sb, out var next))
                {
                    return null;
                }

                i = next;
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
                while (i < pattern.Length && pattern[i] is not ('*' or '?' or '[' or '\\'));

                sb.Append(Regex.Escape(pattern[start..i]));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Translates the character class starting at <paramref name="open"/> (a <c>[</c>) the way
    /// git's wildmatch reads it: a leading <c>!</c> or <c>^</c> negates the class, a <c>]</c>
    /// right after the opening bracket (or negation marker) is a member rather than the end,
    /// a backslash makes the next character a literal member, and <c>a-z</c> is a range.
    /// </summary>
    /// <returns><see langword="false"/> when the class has no closing <c>]</c>.</returns>
    private static bool TryTranslateClass(string pattern, int open, StringBuilder sb, out int next)
    {
        var members = new StringBuilder();
        var j = open + 1;
        var negate = j < pattern.Length && pattern[j] is ('!' or '^');
        if (negate)
        {
            j++;
        }

        var first = true;
        while (j < pattern.Length)
        {
            var ch = pattern[j];
            if (ch == ']' && !first)
            {
                // A class never matches "/" in a path (git's wildmatch, WM_PATHNAME), negated or not.
                sb.Append(negate ? "(?!/)[^" : "(?!/)[").Append(members).Append(']');
                next = j + 1;
                return true;
            }

            if (ch == '\\' && j + 1 < pattern.Length)
            {
                // An escaped member is always literal, including "-", "]", and "^". Letters and
                // digits need no regex escape, and must not get one ("\b" in a regex class
                // is a backspace, "\d" a digit class).
                ch = pattern[j + 1];
                j += 2;
                if (!char.IsLetterOrDigit(ch))
                {
                    members.Append('\\');
                }
            }
            else
            {
                j++;

                // "-" stays unescaped so ranges keep working; the characters that are
                // special inside a regex class are escaped to match themselves.
                if (ch is '\\' or '^' or '[' or ']')
                {
                    members.Append('\\');
                }
            }

            members.Append(ch);
            first = false;
        }

        next = open;
        return false;
    }

    /// <summary>
    /// Drops the run of trailing spaces from <paramref name="line"/> the way git's
    /// <c>trim_trailing_spaces</c> does: only the space character is trimmed (a trailing tab
    /// or other whitespace is part of the pattern), and a space escaped by a backslash ends
    /// the run. Backslashes are read as escape pairs from the start of the line, so in
    /// <c>foo\\ </c> the space follows an escaped backslash and is still trimmed.
    /// </summary>
    /// <param name="line">The raw ignore-file line, without its line terminator.</param>
    internal static string TrimTrailingSpaces(string line)
    {
        var trimFrom = -1;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case ' ':
                    if (trimFrom < 0)
                    {
                        trimFrom = i;
                    }

                    break;
                case '\\':
                    i++;
                    if (i == line.Length)
                    {
                        return line;
                    }

                    trimFrom = -1;
                    break;
                default:
                    trimFrom = -1;
                    break;
            }
        }

        return trimFrom < 0 ? line : line[..trimFrom];
    }

    private static bool TryCompileBody(string line, bool ignoreCase, out Regex regex, out bool directoryOnly)
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
        if (body is null)
        {
            return false;
        }

        var prefix = anchored ? "^" : "(?:^|.*/)";
        try
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (ignoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(prefix + body + "$", options);
        }
        catch (ArgumentException)
        {
            // A character class with no regex equivalent (e.g. a reversed range like
            // "[z-a]"): skip just this pattern, as git tolerates a malformed
            // line, rather than letting one bad line abort the whole scan.
            regex = null!;
            return false;
        }

        return true;
    }
}