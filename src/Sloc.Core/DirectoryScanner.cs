using Microsoft.Extensions.FileSystemGlobbing;
using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Core;

/// <summary>
/// A file discovered by the <see cref="DirectoryScanner"/>, paired with the
/// language it should be analyzed as.
/// </summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Language">The language to analyze the file as.</param>
public sealed record ScannedFile(string Path, LanguageDefinition Language);

/// <summary>
/// The result of a <see cref="DirectoryScanner.Scan"/> call.
/// </summary>
/// <param name="Files">The files that were successfully discovered.</param>
/// <param name="Skipped">Paths that could not be enumerated due to I/O or access errors.</param>
public sealed record ScanResult(IReadOnlyList<ScannedFile> Files, IReadOnlyList<SkippedEntry> Skipped);

/// <summary>
/// Options controlling how <see cref="DirectoryScanner"/> discovers files.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>
    /// Glob patterns of files to exclude, applied on top of the built-in excludes.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// Glob patterns of files to include. When empty, all files are considered.
    /// </summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>
    /// Whether to include files whose extension maps to no known language.
    /// </summary>
    public bool IncludeUnknown
    {
        get; init;
    }

    /// <summary>
    /// Whether to descend into subdirectories. Ignored when explicit includes are supplied.
    /// </summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Whether to honor <c>.gitignore</c> files discovered under the scan root.
    /// </summary>
    public bool RespectGitignore
    {
        get; init;
    }

    /// <summary>
    /// Language display names (e.g. <c>"C#"</c>) to exclude, matched case-insensitively
    /// against the resolved language's <see cref="LanguageDefinition.Name"/>.
    /// </summary>
    public IReadOnlyList<string> ExcludeLangs { get; init; } = [];

    /// <summary>
    /// Language display names (e.g. <c>"C#"</c>) to include. When empty, all resolved
    /// languages are considered. Matched case-insensitively against the resolved
    /// language's <see cref="LanguageDefinition.Name"/>.
    /// </summary>
    public IReadOnlyList<string> IncludeLangs { get; init; } = [];
}

/// <summary>
/// Discovers source files under a path, honoring include/exclude globs and a
/// set of built-in directory excludes.
/// </summary>
public sealed class DirectoryScanner
{
    // Single source of truth for built-in directory excludes; the glob list below is
    // derived from it so the two never drift out of sync.
    private static readonly IReadOnlySet<string> DefaultExcludeDirectoryNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "artifacts", ".git", ".vs", ".vscode", ".idea", "node_modules"
        };

    private static readonly string[] DefaultExcludes =
        DefaultExcludeDirectoryNames.Select(name => $"**/{name}/**").ToArray();

    private static readonly LanguageDefinition UnknownLanguage = new()
    {
        Name = "Other",
        Extensions = []
    };

    /// <summary>
    /// Discovers the files to analyze under <paramref name="root"/>.
    /// </summary>
    /// <param name="root">A file or directory path to scan.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="onFileFound">
    /// An optional callback invoked each time a file is discovered and included in the
    /// scan, receiving the running count and the file's full path. Used to report
    /// progress on long-running scans; has no effect on the result.
    /// </param>
    /// <param name="onGitignoreScan">
    /// An optional callback invoked while searching for <c>.gitignore</c> files (when
    /// <see cref="ScanOptions.RespectGitignore"/> is set), before file discovery begins,
    /// receiving the running count of directories visited and the current directory's
    /// full path. Used to report progress on long-running scans; has no effect on the
    /// result.
    /// </param>
    /// <returns>
    /// A <see cref="ScanResult"/> containing the discovered files and any paths
    /// that could not be enumerated due to I/O or access errors.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The path does not exist.
    /// </exception>
    public ScanResult Scan(
        string root,
        ScanOptions options,
        Action<int, string>? onFileFound = null,
        Action<int, string>? onGitignoreScan = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);

        if (File.Exists(root))
        {
            var single = Resolve(Path.GetFullPath(root), options.IncludeUnknown);
            return single is null || !MatchesLanguageFilter(single.Language, options)
                ? new ScanResult([], [])
                : new ScanResult([single], []);
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Path not found: {root}");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (options.Includes.Count > 0)
        {
            matcher.AddIncludePatterns(options.Includes);
        }
        else
        {
            matcher.AddInclude(options.Recursive ? "**/*" : "*");
        }

        matcher.AddExcludePatterns(DefaultExcludes);
        if (options.Excludes.Count > 0)
        {
            matcher.AddExcludePatterns(options.Excludes);
        }

        // Do not follow directory symlinks/junctions: a self-referential link would
        // otherwise make the matcher's "**" traversal recurse forever. The pre-pass below
        // never recurses into a flagged directory, so it cannot loop either.
        var fullRootForSymlinks = Path.GetFullPath(root);
        foreach (var symlinked in FindSymlinkedDirectories(fullRootForSymlinks))
        {
            var relative = Path.GetRelativePath(fullRootForSymlinks, symlinked).Replace('\\', '/');
            matcher.AddExclude($"**/{relative}/**");
        }

        var gitignore = options.RespectGitignore
            ? GitIgnoreRules.Load(root, DefaultExcludeDirectoryNames, options.Recursive, onGitignoreScan)
            : null;
        var fullRoot = Path.GetFullPath(root);

        var files = new List<ScannedFile>();
        var skipped = new List<SkippedEntry>();
        try
        {
            foreach (var fullPath in matcher.GetResultsInFullPath(root))
            {
                if (gitignore is { IsEmpty: false }
                    && gitignore.IsIgnored(Path.GetRelativePath(fullRoot, fullPath)))
                {
                    continue;
                }

                var scanned = Resolve(fullPath, options.IncludeUnknown);
                if (scanned is not null && MatchesLanguageFilter(scanned.Language, options))
                {
                    files.Add(scanned);
                    onFileFound?.Invoke(files.Count, scanned.Path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedEntry(root, ex.Message));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new ScanResult(files, skipped);
    }

    /// <summary>
    /// Resolves an explicit list of file paths for analysis, bypassing directory
    /// globbing, recursion, and <c>.gitignore</c> filtering. Intended for
    /// <c>--list-file</c>/stdin input, where the caller has already decided exactly which
    /// files to analyze.
    /// </summary>
    /// <param name="paths">The file paths to resolve.</param>
    /// <param name="options">
    /// The scan options; only <see cref="ScanOptions.IncludeUnknown"/>,
    /// <see cref="ScanOptions.IncludeLangs"/>, and <see cref="ScanOptions.ExcludeLangs"/>
    /// apply.
    /// </param>
    /// <returns>
    /// A <see cref="ScanResult"/> containing the resolved files and an entry in
    /// <see cref="ScanResult.Skipped"/> for every path that does not exist.
    /// </returns>
    public ScanResult ScanFiles(IEnumerable<string> paths, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<ScannedFile>();
        var skipped = new List<SkippedEntry>();

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                skipped.Add(new SkippedEntry(path, "not found"));
                continue;
            }

            var scanned = Resolve(Path.GetFullPath(path), options.IncludeUnknown);
            if (scanned is not null && MatchesLanguageFilter(scanned.Language, options))
            {
                files.Add(scanned);
            }
        }

        return new ScanResult(files, skipped);
    }

    /// <summary>
    /// Finds directories under <paramref name="root"/> that are symlinks/junctions, so
    /// callers can exclude them from traversal rather than risk following a
    /// self-referential link forever. Never recurses into a symlinked directory itself.
    /// </summary>
    private static IEnumerable<string> FindSymlinkedDirectories(string root)
    {
        var found = new List<string>();
        CollectSymlinkedDirectories(root, found);
        return found;
    }

    private static void CollectSymlinkedDirectories(string directory, List<string> found)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var subdirectory in entries)
        {
            var name = Path.GetFileName(subdirectory);
            if (DefaultExcludeDirectoryNames.Contains(name))
            {
                continue;
            }

            if (new DirectoryInfo(subdirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                found.Add(subdirectory);
                continue;
            }

            CollectSymlinkedDirectories(subdirectory, found);
        }
    }

    private static bool MatchesLanguageFilter(LanguageDefinition language, ScanOptions options)
    {
        if (options.IncludeLangs.Count > 0
            && !options.IncludeLangs.Contains(language.Name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return options.ExcludeLangs.Count == 0
            || !options.ExcludeLangs.Contains(language.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static ScannedFile? Resolve(string path, bool includeUnknown)
    {
        if (LanguageRegistry.TryGetByPath(path, out var language))
        {
            return new ScannedFile(path, language);
        }

        if (includeUnknown)
        {
            return new ScannedFile(path, UnknownLanguage);
        }

        return null;
    }
}