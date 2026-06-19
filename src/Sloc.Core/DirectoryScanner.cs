using Microsoft.Extensions.FileSystemGlobbing;
using Sloc.Core.Languages;

namespace Sloc.Core;

/// <summary>
/// A file discovered by the <see cref="DirectoryScanner"/>, paired with the
/// language it should be analyzed as.
/// </summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Language">The language to analyze the file as.</param>
public sealed record ScannedFile(string Path, LanguageDefinition Language);

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
}

/// <summary>
/// Discovers source files under a path, honoring include/exclude globs and a
/// set of built-in directory excludes.
/// </summary>
public sealed class DirectoryScanner
{
    private static readonly string[] DefaultExcludes =
    [
        "**/bin/**",
        "**/obj/**",
        "**/artifacts/**",
        "**/.git/**",
        "**/.vs/**",
        "**/.vscode/**",
        "**/.idea/**",
        "**/node_modules/**"
    ];

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
    /// <returns>
    /// The discovered files, ordered by path.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The path does not exist.
    /// </exception>
    public IReadOnlyList<ScannedFile> Scan(string root, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);

        if (File.Exists(root))
        {
            var single = Resolve(Path.GetFullPath(root), options.IncludeUnknown);
            return single is null ? Array.Empty<ScannedFile>() : new[] { single };
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

        var files = new List<ScannedFile>();
        foreach (var fullPath in matcher.GetResultsInFullPath(root))
        {
            var scanned = Resolve(fullPath, options.IncludeUnknown);
            if (scanned is not null)
            {
                files.Add(scanned);
            }
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
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