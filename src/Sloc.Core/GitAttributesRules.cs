using System.Text.RegularExpressions;

namespace Sloc.Core;

/// <summary>
/// Evaluates whether a path is marked vendored or generated according to a set of
/// <c>.gitattributes</c> files, recognizing the <c>linguist-vendored</c> and
/// <c>linguist-generated</c> attributes (GitHub Linguist's convention for third-party or
/// machine-generated code that should be excluded from language statistics), also honored
/// by tools such as <c>cloc</c> and <c>scc</c>.
/// </summary>
/// <remarks>
/// A pragmatic implementation: only the two attributes above are recognized (other
/// attributes on a matching line are ignored), and nested <c>.gitattributes</c> files are
/// combined by last-match-wins across the whole set, ordered root-first, rather than git's
/// full per-directory precedence rules.
/// </remarks>
public sealed class GitAttributesRules
{
    private readonly Dictionary<string, bool> _cache = new();
    private readonly IReadOnlyList<AttributesFile> _files;

    private GitAttributesRules(IReadOnlyList<AttributesFile> files)
    {
        _files = files;
    }

    /// <summary>
    /// Gets a value indicating whether any recognized patterns were loaded.
    /// </summary>
    public bool IsEmpty => _files.Count == 0;

    /// <summary>
    /// Parses the supplied attributes-file lines as a single <c>.gitattributes</c> rooted at
    /// <paramref name="baseDirectory"/> (relative to the scan root, <c>/</c>-separated).
    /// Intended for tests.
    /// </summary>
    /// <param name="baseDirectory">The base directory the patterns are relative to.</param>
    /// <param name="lines">The raw attributes-file lines.</param>
    /// <returns>The loaded rule set.</returns>
    public static GitAttributesRules FromLines(string baseDirectory, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(lines);

        var patterns = CompilePatterns(lines);
        return new GitAttributesRules(patterns.Count == 0
            ? []
            : [new AttributesFile(NormalizeBase(baseDirectory), patterns)]);
    }

    /// <summary>
    /// Discovers and loads every <c>.gitattributes</c> file under <paramref name="root"/>,
    /// skipping the supplied excluded directory names.
    /// </summary>
    /// <param name="root">The scan root directory.</param>
    /// <param name="excludedDirectoryNames">
    /// Directory names not to descend into when searching for attributes files (e.g. <c>.git</c>).
    /// </param>
    /// <param name="recursive">
    /// Whether to search subdirectories for nested <c>.gitattributes</c> files. When
    /// <see langword="false"/>, only the <paramref name="root"/> directory itself is checked,
    /// matching the shallow file discovery performed by the scanner.
    /// </param>
    /// <param name="onDirectoryVisited">
    /// Invoked once for every directory visited while searching for <c>.gitattributes</c>
    /// files, with a running visited-directory count and the directory path.
    /// </param>
    /// <returns>The loaded rule set (possibly empty).</returns>
    public static GitAttributesRules Load(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        Action<int, string>? onDirectoryVisited = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        var walk = ScanTreeWalker.Walk(
            root, excludedDirectoryNames, recursive, followSymlinks: false,
            collectGitignore: false, collectGitattributes: true, collectFiles: false, onDirectoryVisited);

        return FromFiles(walk.AttributesFiles);
    }

    /// <summary>
    /// Determines whether the given path (relative to the scan root, using either
    /// separator) is marked <c>linguist-vendored</c> or <c>linguist-generated</c>.
    /// </summary>
    /// <param name="relativePath">The path relative to the scan root.</param>
    /// <returns><see langword="true"/> if the path should be excluded.</returns>
    public bool IsVendoredOrGenerated(string relativePath)
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

        if (_cache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        bool? vendored = null;
        bool? generated = null;
        foreach (var file in _files)
        {
            if (!file.TryGetRelativePath(normalized, out var relativeToBase))
            {
                continue;
            }

            foreach (var pattern in file.Patterns)
            {
                if (!pattern.Regex.IsMatch(relativeToBase))
                {
                    continue;
                }

                if (pattern.Vendored.HasValue)
                {
                    vendored = pattern.Vendored;
                }

                if (pattern.Generated.HasValue)
                {
                    generated = pattern.Generated;
                }
            }
        }

        var result = vendored == true || generated == true;
        _cache[normalized] = result;
        return result;
    }

    internal static List<AttributePattern> CompilePatterns(IEnumerable<string> lines)
    {
        var patterns = new List<AttributePattern>();
        foreach (var line in lines)
        {
            if (AttributePattern.TryCompile(line, out var pattern))
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }

    /// <summary>
    /// Builds a rule set from <c>.gitattributes</c> files already discovered by a
    /// <see cref="ScanTreeWalker"/> pass, without walking the directory tree again.
    /// </summary>
    internal static GitAttributesRules FromFiles(IReadOnlyList<AttributesFile> files) =>
        new(files.OrderBy(file => file.BaseDirectory.Length).ToList());

    internal static string NormalizeBase(string baseDirectory)
    {
        var normalized = baseDirectory.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    internal sealed class AttributesFile(string baseDirectory, IReadOnlyList<AttributePattern> patterns)
    {
        public string BaseDirectory { get; } = baseDirectory;

        public IReadOnlyList<AttributePattern> Patterns { get; } = patterns;

        public bool TryGetRelativePath(string path, out string relativeToBase) =>
            RelativePathResolver.TryGetRelativePath(BaseDirectory, path, out relativeToBase);
    }
}

/// <summary>
/// A single compiled <c>.gitattributes</c> line that sets <c>linguist-vendored</c> and/or
/// <c>linguist-generated</c>. Lines that set neither attribute are not represented.
/// </summary>
internal sealed class AttributePattern
{
    private const string GeneratedAttribute = "linguist-generated";
    private const string VendoredAttribute = "linguist-vendored";

    private AttributePattern(Regex regex, bool? vendored, bool? generated)
    {
        Regex = regex;
        Vendored = vendored;
        Generated = generated;
    }

    public bool? Generated
    {
        get;
    }

    public Regex Regex
    {
        get;
    }

    public bool? Vendored
    {
        get;
    }

    public static bool TryCompile(string rawLine, out AttributePattern pattern)
    {
        pattern = null!;

        var line = rawLine.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        bool? vendored = null;
        bool? generated = null;
        for (var i = 1; i < tokens.Length; i++)
        {
            ApplyAttribute(tokens[i], VendoredAttribute, ref vendored);
            ApplyAttribute(tokens[i], GeneratedAttribute, ref generated);
        }

        if (vendored is null && generated is null)
        {
            return false;
        }

        if (!GitIgnorePattern.TryCompilePattern(tokens[0], out var regex, out _))
        {
            return false;
        }

        pattern = new AttributePattern(regex, vendored, generated);
        return true;
    }

    private static void ApplyAttribute(string token, string name, ref bool? value)
    {
        if (token.Equals(name, StringComparison.Ordinal))
        {
            value = true;
        }
        else if (token.Equals("-" + name, StringComparison.Ordinal))
        {
            value = false;
        }
        else if (token.StartsWith(name + "=", StringComparison.Ordinal))
        {
            var explicitValue = token[(name.Length + 1)..];
            value = !(explicitValue.Equals("false", StringComparison.OrdinalIgnoreCase) || explicitValue == "0");
        }
    }
}