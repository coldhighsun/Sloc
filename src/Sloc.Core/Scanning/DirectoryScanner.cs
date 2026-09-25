using Microsoft.Extensions.FileSystemGlobbing;
using Sloc.Core.Languages;
using Sloc.Core.Models;

namespace Sloc.Core.Scanning;

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
/// A file passed to <see cref="DirectoryScanner.ScanSnapshot"/>: where it actually is, and
/// the path it has relative to the scan root of the tree it belongs to.
/// </summary>
/// <param name="FullPath">The file's actual location on disk.</param>
/// <param name="RelativePath">The file's <c>/</c>-separated path relative to the scan root.</param>
public sealed record SnapshotEntry(string FullPath, string RelativePath);

/// <summary>
/// The repository a <see cref="DirectoryScanner.ScanSnapshot"/> tree belongs to, so the
/// <c>.gitignore</c>/<c>.gitattributes</c> files above its scan root come from the same
/// snapshot rather than from the disk.
/// </summary>
/// <param name="ScanPrefix">The scan root's <c>/</c>-separated path relative to the repository root, empty for the root itself.</param>
/// <param name="Files">
/// The snapshot's files with repository-relative paths; only the <c>.gitignore</c> and
/// <c>.gitattributes</c> files in the directories above <paramref name="ScanPrefix"/> are used.
/// </param>
public sealed record SnapshotRepository(string ScanPrefix, IReadOnlyList<SnapshotEntry> Files);

/// <summary>
/// Options controlling how <see cref="DirectoryScanner"/> discovers files.
/// </summary>
public sealed class ScanOptions
{
    /// <summary>
    /// Language display names (e.g. <c>"C#"</c>) to exclude, matched case-insensitively
    /// against the resolved language's <see cref="LanguageDefinition.Name"/>.
    /// </summary>
    public IReadOnlyList<string> ExcludeLangs { get; init; } = [];

    /// <summary>
    /// Glob patterns of files to exclude, applied on top of the built-in excludes.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// Whether to include symlinked/junctioned directories and symlinked files rather than
    /// skip them. Defaults to <see langword="false"/>. A directory symlink whose resolved
    /// target would loop back onto a directory already on the path from the scan root is
    /// always skipped regardless of this setting, including cycles formed by two or more
    /// distinct symlinks chained together, and a directory symlink whose target contains one
    /// of those directories (e.g. a link to the scan root's parent). A symlink whose target
    /// lies at or under the scan root is likewise skipped, since the normal tree walk already
    /// covers those files and following the link would double-count them; only targets
    /// outside the scan root are actually followed. Paths are compared after resolving every
    /// link along them, so this also holds when the scan root is itself reached through a
    /// symlink/junction, and an external file or directory reached through more than one
    /// link (including links with overlapping targets) is counted only once. Has no effect
    /// when the scan <c>root</c> passed
    /// to <see cref="DirectoryScanner.Scan"/> is itself an explicit single file, which is
    /// always analyzed regardless of whether it is a symlink.
    /// </summary>
    public bool FollowSymlinks
    {
        get; init;
    }

    /// <summary>
    /// Language display names (e.g. <c>"C#"</c>) to include. When empty, all resolved
    /// languages are considered. Matched case-insensitively against the resolved
    /// language's <see cref="LanguageDefinition.Name"/>.
    /// </summary>
    public IReadOnlyList<string> IncludeLangs { get; init; } = [];

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
    /// Whether to exclude files marked <c>linguist-vendored</c> or <c>linguist-generated</c>
    /// in <c>.gitattributes</c> files discovered under the scan root or in the directories
    /// above it up to the root of the repository it belongs to.
    /// </summary>
    public bool RespectGitAttributes { get; init; } = true;

    /// <summary>
    /// Whether to honor <c>.gitignore</c> files discovered under the scan root or in the
    /// directories above it up to the root of the repository it belongs to, along with that
    /// repository's <c>info/exclude</c> and the global excludes file. When the scan root is
    /// inside a repository, this also skips nested repositories (submodules), whose files
    /// are not part of it.
    /// </summary>
    public bool RespectGitignore
    {
        get; init;
    }

    /// <summary>
    /// Whether to skip nested repositories (submodules) when the scan root is inside a
    /// repository even if <see cref="RespectGitignore"/> is not set, as when comparing against
    /// a git commit, which records a submodule without any of its files. Nested repositories
    /// are always skipped when <see cref="RespectGitignore"/> is set.
    /// </summary>
    public bool SkipNestedRepositories
    {
        get; init;
    }
}

/// <summary>
/// Discovers source files under a path, honoring include/exclude globs and a
/// set of built-in directory excludes.
/// </summary>
public sealed class DirectoryScanner
{
    private static readonly string[] DefaultExcludeDirectoryNameList =
        ["bin", "obj", "artifacts", ".git", ".vs", ".vscode", ".idea", "node_modules"];

    // Built fresh per scan so directory-name exclusion honors the same resolved
    // ignoreCase (git core.ignoreCase/platform) as every other rule in the scan, instead
    // of always matching case-insensitively.
    private static IReadOnlySet<string> ExcludeDirectoryNames(bool ignoreCase) =>
        new HashSet<string>(DefaultExcludeDirectoryNameList, IgnoreCaseComparer.Comparer(ignoreCase));

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
    /// An optional callback invoked once per directory visited during the tree walk that
    /// discovers <c>.gitignore</c>/<c>.gitattributes</c> files and candidate file paths,
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
            var fullPath = Path.GetFullPath(root);
            return ScanSingleFile(fullPath, fullPath, options, repository: null);
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Path not found: {root}");
        }

        var fullRoot = Path.GetFullPath(root);
        var walkRecursive = WalksRecursively(options);
        var workTree = GitWorkTree.Find(fullRoot);
        var ignoreCase = GitIgnoreRules.ResolveIgnoreCase(workTree);

        // A single tree walk discovers .gitignore files, .gitattributes files, candidate
        // file paths, and symlink/junction loop protection all at once, instead of walking
        // the same directory tree separately for each concern. Inside a repository, git never
        // descends into a nested repository (a submodule, or a clone it doesn't track), so
        // with .gitignore honored neither does the walk.
        var walk = ScanTreeWalker.Walk(
            root, ExcludeDirectoryNames(ignoreCase), walkRecursive, options.FollowSymlinks, ignoreCase,
            options.RespectGitignore, options.RespectGitAttributes, collectFiles: true, onGitignoreScan,
            skipNestedRepositories: (options.RespectGitignore || options.SkipNestedRepositories) && workTree is not null);

        var scanPrefix = workTree?.RelativeDirectory(fullRoot) ?? string.Empty;
        var ancestors = CreateRuleFiles(ignoreCase, options);
        ancestors.ReadAncestorsFromDisk(workTree, scanPrefix);
        var (gitignore, gitattributes) = BuildRules(
            workTree, ignoreCase, scanPrefix, walk.GitignoreFiles, walk.AttributesFiles, ancestors, options);

        // Ordinal (case-sensitive) comparer: Matcher.Match's OrdinalIgnoreCase comparison is
        // for pattern matching only, its Files results echo back the exact strings it was
        // given, so an ignore-case map here would collide two paths differing only by case
        // (possible on case-sensitive filesystems) and silently drop one from the scan.
        var pathByRelative = new Dictionary<string, string>(walk.FilePaths.Count);
        foreach (var fullPath in walk.FilePaths)
        {
            if (!options.FollowSymlinks && walk.SymlinkedFilePaths.Contains(fullPath))
            {
                continue;
            }

            pathByRelative[Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/')] = fullPath;
        }

        var files = FilterCandidates(pathByRelative, options, gitignore, gitattributes, ignoreCase, onFileFound)
            .ConvertAll(static candidate => candidate.File);
        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new ScanResult(files, [.. walk.Skipped, .. ancestors.Skipped]);
    }

    /// <summary>
    /// Applies <see cref="Scan"/>'s directory-scan filtering to files that are not laid out
    /// under a real directory tree, such as blobs extracted from a git commit into temporary
    /// files: each entry pairs the file's actual location with the path it has relative to
    /// the scan root. The include/exclude globs (and <see cref="ScanOptions.Recursive"/>), the
    /// built-in excluded directories, the <c>.gitignore</c>/<c>.gitattributes</c> files found
    /// among the entries themselves, and the language filters are applied exactly as a
    /// <see cref="Scan"/> of that tree at <paramref name="root"/> would apply them, so the two
    /// results are comparable (as <c>--compare-to</c> requires).
    /// </summary>
    /// <param name="root">
    /// The real directory the relative paths are rooted at, used (as by <see cref="Scan"/>)
    /// to locate the global git excludes file and the repository's config and
    /// <c>info/exclude</c>, and, unless <paramref name="repository"/> is given, the rule files
    /// above the scan root in its repository.
    /// </param>
    /// <param name="entries">The files, each with its scan-root-relative path.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="repository">
    /// The repository the entries belong to, whose rule files above the scan root replace the
    /// ones <see cref="Scan"/> would read from the disk above <paramref name="root"/>.
    /// </param>
    /// <returns>
    /// A <see cref="ScanResult"/> with the entries that pass every filter, ordered by their
    /// relative path, and an entry in <see cref="ScanResult.Skipped"/> for every rule file
    /// that could not be read.
    /// </returns>
    public ScanResult ScanSnapshot(
        string root,
        IReadOnlyList<SnapshotEntry> entries,
        ScanOptions options,
        SnapshotRepository? repository = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var walkRecursive = WalksRecursively(options);
        var workTree = GitWorkTree.Find(root);
        var ignoreCase = GitIgnoreRules.ResolveIgnoreCase(workTree);
        var walked = CreateRuleFiles(ignoreCase, options);
        var pathByRelative = new Dictionary<string, string>(entries.Count);
        var excludeDirectoryNames = ExcludeDirectoryNames(ignoreCase);

        foreach (var entry in entries)
        {
            var relativePath = entry.RelativePath.Replace('\\', '/').Trim('/');
            pathByRelative[relativePath] = entry.FullPath;

            // Only the rule files a directory walk would have visited.
            if (walked.TryGetRuleFile(relativePath, out var directory, out var kind)
                && ScanTreeWalker.Visits(directory, excludeDirectoryNames, walkRecursive))
            {
                walked.Read(entry.FullPath, directory, kind);
            }
        }

        var ancestors = CreateRuleFiles(ignoreCase, options);
        string scanPrefix;
        if (repository is null)
        {
            scanPrefix = workTree?.RelativeDirectory(root) ?? string.Empty;
            ancestors.ReadAncestorsFromDisk(workTree, scanPrefix);
        }
        else
        {
            scanPrefix = repository.ScanPrefix.Replace('\\', '/').Trim('/');
            ancestors.ReadFromSnapshot(
                repository.Files, GitWorkTree.AncestorDirectories(scanPrefix).ToHashSet(StringComparer.Ordinal));
        }

        var (gitignore, gitattributes) = BuildRules(
            workTree, ignoreCase, scanPrefix, walked.GitignoreFiles, walked.AttributesFiles, ancestors, options);
        var candidates = FilterCandidates(pathByRelative, options, gitignore, gitattributes, ignoreCase, onFileFound: null);
        candidates.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return new ScanResult(
            candidates.ConvertAll(static candidate => candidate.File), [.. walked.Skipped, .. ancestors.Skipped]);
    }

    /// <summary>
    /// Applies <see cref="Scan"/>'s single-file filtering for <paramref name="path"/> (the
    /// <c>.gitignore</c>/<c>.gitattributes</c> files of its directory and the ones above it in
    /// its repository, the include/exclude globs, and the language filters) to
    /// <paramref name="snapshotPath"/>, a copy of that file's content stored elsewhere (e.g.
    /// extracted from a git commit), so a single-file <c>--compare-to</c> baseline is kept or
    /// dropped exactly as the current side is.
    /// </summary>
    /// <param name="path">The real file path the filters are evaluated against.</param>
    /// <param name="snapshotPath">The location of the content to analyze.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="repository">
    /// The repository the file belongs to, with <see cref="SnapshotRepository.ScanPrefix"/>
    /// set to the file's own repository-relative path, whose rule files replace the ones
    /// <see cref="Scan"/> would read from the disk; when omitted, they are read from the disk.
    /// </param>
    /// <returns>
    /// A <see cref="ScanResult"/> with <paramref name="snapshotPath"/> if <paramref name="path"/>
    /// passes every filter, otherwise no files, and an entry in <see cref="ScanResult.Skipped"/>
    /// for every rule file that could not be read.
    /// </returns>
    public ScanResult ScanSnapshotFile(
        string path,
        string snapshotPath,
        ScanOptions options,
        SnapshotRepository? repository = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(snapshotPath);
        ArgumentNullException.ThrowIfNull(options);

        return ScanSingleFile(Path.GetFullPath(path), snapshotPath, options, repository);
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
    /// <see cref="ScanResult.Skipped"/> for every path that does not exist. A file listed
    /// more than once (even spelled differently, e.g. <c>a.cs</c> and <c>./a.cs</c>) is
    /// resolved only once, at its first occurrence, so it is not counted twice.
    /// </returns>
    public ScanResult ScanFiles(IEnumerable<string> paths, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<ScannedFile>();
        var skipped = new List<SkippedEntry>();
        var seen = new HashSet<string>(SymlinkGuard.FileSystemPathComparer);

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                skipped.Add(new SkippedEntry(path, "not found"));
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (!seen.Add(fullPath))
            {
                continue;
            }

            var scanned = Resolve(fullPath, options.IncludeUnknown);
            if (scanned is not null && MatchesLanguageFilter(scanned.Language, options))
            {
                files.Add(scanned);
            }
        }

        return new ScanResult(files, skipped);
    }

    /// <summary>
    /// Whether a directory scan descends into subdirectories. <see cref="ScanOptions.Recursive"/>
    /// is ignored when explicit includes are supplied: the include patterns already scope the
    /// candidates, but the walk must still descend to find files an include pattern like
    /// <c>**/*.cs</c> could match below the root.
    /// </summary>
    private static bool WalksRecursively(ScanOptions options) =>
        options.Recursive || options.Includes.Count > 0;

    /// <summary>
    /// Creates a collector for the rule files <paramref name="options"/> say to honor.
    /// </summary>
    /// <param name="ignoreCase">Whether patterns match case-insensitively.</param>
    /// <param name="options">The scan options.</param>
    private static GitRuleFiles CreateRuleFiles(bool ignoreCase, ScanOptions options) =>
        new(ignoreCase, options.RespectGitignore, options.RespectGitAttributes);

    /// <summary>
    /// Builds the <c>.gitignore</c> and <c>.gitattributes</c> rule sets a scan applies, each
    /// <see langword="null"/> when <paramref name="options"/> say not to honor it.
    /// </summary>
    /// <param name="workTree">The working tree the scan root belongs to, if any.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively.</param>
    /// <param name="scanPrefix">The scan root's <c>/</c>-separated repository-relative directory.</param>
    /// <param name="walkedGitignoreFiles">The <c>.gitignore</c> files under the scan root, with scan-root-relative bases.</param>
    /// <param name="walkedAttributesFiles">The <c>.gitattributes</c> files under the scan root, with scan-root-relative bases.</param>
    /// <param name="ancestors">The rule files above the scan root, with repository-relative bases.</param>
    /// <param name="options">The scan options.</param>
    private static (GitIgnoreRules? Gitignore, GitAttributesRules? Gitattributes) BuildRules(
        GitWorkTree? workTree,
        bool ignoreCase,
        string scanPrefix,
        IReadOnlyList<GitIgnoreRules.GitIgnoreFile> walkedGitignoreFiles,
        IReadOnlyList<GitAttributesRules.AttributesFile> walkedAttributesFiles,
        GitRuleFiles ancestors,
        ScanOptions options) =>
        (options.RespectGitignore
                ? GitIgnoreRules.FromWalk(workTree, walkedGitignoreFiles, ignoreCase, scanPrefix, ancestors.GitignoreFiles)
                : null,
            options.RespectGitAttributes
                ? GitAttributesRules.FromWalk(walkedAttributesFiles, ignoreCase, scanPrefix, ancestors.AttributesFiles)
                : null);

    /// <summary>
    /// <see cref="Scan"/>'s handling of an explicit single-file root: the file is dropped if
    /// the <c>.gitignore</c>/<c>.gitattributes</c> files of its own directory and the ones
    /// above it in its repository exclude it, the include/exclude globs reject it, or its
    /// language is filtered out. Its directory is the scan root, so (as for a directory scan)
    /// a pattern ignoring that directory or one above it does not drop the file.
    /// </summary>
    /// <param name="fullPath">The file's full path, which every filter is evaluated against.</param>
    /// <param name="contentPath">The path recorded on the result (the file whose content is analyzed).</param>
    /// <param name="options">The scan options.</param>
    /// <param name="repository">
    /// The repository snapshot the rule files come from, with the file's repository-relative
    /// path as <see cref="SnapshotRepository.ScanPrefix"/>, or <see langword="null"/> to read them from the disk.
    /// </param>
    private static ScanResult ScanSingleFile(
        string fullPath,
        string contentPath,
        ScanOptions options,
        SnapshotRepository? repository)
    {
        var parentDir = Path.GetDirectoryName(fullPath) ?? fullPath;
        var fileName = Path.GetFileName(fullPath);
        List<SkippedEntry> skipped = [];

        if (options.RespectGitignore || options.RespectGitAttributes)
        {
            var workTree = GitWorkTree.Find(parentDir);
            var ignoreCase = GitIgnoreRules.ResolveIgnoreCase(workTree);
            var ruleFiles = CreateRuleFiles(ignoreCase, options);
            string scanPrefix;
            if (repository is null)
            {
                // The file's own directory and every one above it up to the repository root
                // (just its own directory outside a repository), read from the disk.
                scanPrefix = workTree?.RelativeDirectory(parentDir) ?? string.Empty;
                ruleFiles.ReadFromDisk(
                    workTree?.Root ?? parentDir,
                    GitWorkTree.AncestorDirectories(RelativePathResolver.Combine(scanPrefix, fileName)));
            }
            else
            {
                var repositoryPath = repository.ScanPrefix.Replace('\\', '/').Trim('/');
                var slash = repositoryPath.LastIndexOf('/');
                scanPrefix = slash < 0 ? string.Empty : repositoryPath[..slash];
                ruleFiles.ReadFromSnapshot(
                    repository.Files, GitWorkTree.AncestorDirectories(repositoryPath).ToHashSet(StringComparer.Ordinal));
            }

            skipped = ruleFiles.Skipped;
            var (gitignore, gitattributes) = BuildRules(workTree, ignoreCase, scanPrefix, [], [], ruleFiles, options);
            if ((gitignore is { IsEmpty: false } && gitignore.IsIgnored(fileName))
                || (gitattributes is { IsEmpty: false } && gitattributes.IsVendoredOrGenerated(fileName)))
            {
                return new ScanResult([], skipped);
            }
        }

        if (options.Includes.Count > 0 || options.Excludes.Count > 0)
        {
            // Match both the file name (its path relative to its parent directory, as a
            // scan of that directory would, so "*.cs" works) and its path relative to the
            // current directory (as a scan of "." would, so "src/*.cs" works, and a
            // directory above the current one can't trigger e.g. "**/bin/**"). A file
            // outside the current directory has no such relative path, so it falls back
            // to its path with the drive/UNC root stripped, which still lets
            // directory-segment patterns like "**/sub/*.cs" match it.
            var relativeToCwd = Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath);
            var outsideCwd = Path.IsPathRooted(relativeToCwd)
                || relativeToCwd == ".."
                || relativeToCwd.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var directoryTarget = outsideCwd
                ? fullPath[(Path.GetPathRoot(fullPath) ?? string.Empty).Length..]
                : relativeToCwd;
            string[] matchTargets = [fileName, directoryTarget.Replace('\\', '/')];
            if ((options.Includes.Count > 0 && !AnyGlobMatches(options.Includes, matchTargets))
                || (options.Excludes.Count > 0 && AnyGlobMatches(options.Excludes, matchTargets)))
            {
                return new ScanResult([], skipped);
            }
        }

        // The language comes from the real file name; the result points at the content.
        var single = Resolve(fullPath, options.IncludeUnknown);
        return single is null || !MatchesLanguageFilter(single.Language, options)
            ? new ScanResult([], skipped)
            : new ScanResult([single with { Path = contentPath }], skipped);
    }

    /// <summary>
    /// The filtering shared by <see cref="Scan"/> and <see cref="ScanSnapshot"/>: matches the
    /// candidates' scan-root-relative paths against the include/exclude globs and built-in
    /// excludes, drops paths excluded by <paramref name="gitignore"/> or marked
    /// vendored/generated by <paramref name="gitattributes"/>, then resolves each remaining
    /// file's language and applies the language filters.
    /// </summary>
    /// <param name="pathByRelative">Each candidate's full path, keyed by its <c>/</c>-separated relative path.</param>
    /// <param name="options">The scan options.</param>
    /// <param name="gitignore">The <c>.gitignore</c> rules to apply, or <see langword="null"/>.</param>
    /// <param name="gitattributes">The <c>.gitattributes</c> rules to apply, or <see langword="null"/>.</param>
    /// <param name="ignoreCase">
    /// Whether a built-in excluded directory name matches a differently-cased directory (git's
    /// <c>core.ignoreCase</c>), applied the same way <see cref="ScanTreeWalker.Visits"/> already
    /// applies it when a real directory walk is available.
    /// </param>
    /// <param name="onFileFound">Invoked with the running count and path of each included file.</param>
    /// <returns>Each included file, paired with its relative path.</returns>
    private static List<(string RelativePath, ScannedFile File)> FilterCandidates(
        Dictionary<string, string> pathByRelative,
        ScanOptions options,
        GitIgnoreRules? gitignore,
        GitAttributesRules? gitattributes,
        bool ignoreCase,
        Action<int, string>? onFileFound)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (options.Includes.Count > 0)
        {
            matcher.AddIncludePatterns(options.Includes);
        }
        else
        {
            matcher.AddInclude(options.Recursive ? "**/*" : "*");
        }

        if (options.Excludes.Count > 0)
        {
            matcher.AddExcludePatterns(options.Excludes);
        }

        // Built-in directory excludes are applied separately below, by directory-name segment
        // against excludeDirectoryNames, rather than through the Matcher's own (always
        // case-insensitive) glob matching: unlike Matcher, that check can honor ignoreCase.
        var excludeDirectoryNames = ExcludeDirectoryNames(ignoreCase);

        // Batch the glob match across all candidate paths instead of re-running Matcher.Match
        // (which rebuilds an InMemoryDirectoryInfo internally) once per file. The reverse map
        // also lets the loop below iterate only the matched subset instead of every candidate
        // file, so excludes that filter out most files don't cost a second full-length pass.
        var files = new List<(string RelativePath, ScannedFile File)>();
        foreach (var matchedFile in matcher.Match(pathByRelative.Keys).Files)
        {
            var relativePath = matchedFile.Path;
            if (!pathByRelative.TryGetValue(relativePath, out var fullPath))
            {
                continue;
            }

            var directory = relativePath[..Math.Max(0, relativePath.LastIndexOf('/'))];
            if (!ScanTreeWalker.Visits(directory, excludeDirectoryNames, recursive: true))
            {
                continue;
            }

            if (gitignore is { IsEmpty: false } && gitignore.IsIgnored(relativePath))
            {
                continue;
            }

            if (gitattributes is { IsEmpty: false } && gitattributes.IsVendoredOrGenerated(relativePath))
            {
                continue;
            }

            var scanned = Resolve(fullPath, options.IncludeUnknown);
            if (scanned is not null && MatchesLanguageFilter(scanned.Language, options))
            {
                files.Add((relativePath, scanned));
                onFileFound?.Invoke(files.Count, scanned.Path);
            }
        }

        return files;
    }

    private static bool AnyGlobMatches(IReadOnlyList<string> patterns, IEnumerable<string> paths)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(patterns);
        return matcher.Match(paths).HasMatches;
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