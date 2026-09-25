using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Sloc.Core;

/// <summary>
/// A file dumped from a git tree entry into a temporary directory, paired with its
/// original git-relative path.
/// </summary>
/// <param name="TempPath">The absolute path of the dumped file on disk.</param>
/// <param name="GitPath">The forward-slash, repo-root-relative path as reported by git.</param>
public sealed record GitSnapshotFile(string TempPath, string GitPath);

/// <summary>
/// The result of extracting a git commit/tree-ish into a temporary directory.
/// Disposing deletes the temporary directory and everything under it.
/// </summary>
/// <param name="TempRoot">The temporary directory the blobs were dumped into.</param>
/// <param name="RelativePrefix">
/// The repo-root-relative, forward-slash path of the directory (or file) <c>repoPathHint</c>
/// pointed at, as resolved by <c>git rev-parse --show-prefix</c> (empty when it was the repo
/// root itself; for a file, its directory's prefix plus the file name). Resolving this via git rather than comparing filesystem paths in .NET avoids
/// mismatches when the hint path and the repo root differ only by an unresolved symlink
/// (e.g. macOS's <c>/var/folders/...</c> vs. its <c>/private/var/folders/...</c> real path).
/// Lets a caller that was given a subdirectory of the repo filter <see cref="Files"/> to it.
/// </param>
/// <param name="Files">The dumped files, paired with their git-relative paths.</param>
/// <param name="Skipped">
/// Tree entries that were not dumped (symlinks, submodules/gitlinks, or blobs that
/// could not be read), together with the reason.
/// </param>
public sealed record GitSnapshot(
    string TempRoot,
    string RelativePrefix,
    IReadOnlyList<GitSnapshotFile> Files,
    IReadOnlyList<Models.SkippedEntry> Skipped) : IDisposable
{
    /// <summary>
    /// Deletes <see cref="TempRoot"/> and everything under it, ignoring errors.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a locked/removed temp file must not fail the run.
        }
    }
}

/// <summary>
/// Thrown when a git commit/tree-ish cannot be resolved or extracted, e.g. because
/// <c>git</c> is not on <c>PATH</c>, the path is not inside a git repository, or the
/// given commit-ish does not exist.
/// </summary>
public sealed class GitSnapshotException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitSnapshotException"/> class.
    /// </summary>
    /// <param name="message">A message describing why extraction failed.</param>
    public GitSnapshotException(string message) : base(message)
    {
    }
}

/// <summary>
/// Extracts the full file tree of a git commit/tree-ish into a temporary directory by
/// shelling out to the <c>git</c> executable, without checking out the commit.
/// </summary>
public sealed class GitSnapshotExtractor
{
    /// <summary>
    /// The longest file name, in UTF-8 bytes, <see cref="SanitizeFileName"/> produces. 255 is
    /// the per-component limit on Linux and macOS (in bytes) and on Windows (in UTF-16 code
    /// units, of which a name never has more than it has UTF-8 bytes), so this fits everywhere.
    /// </summary>
    internal const int MaxFileNameBytes = 255;

    private static readonly SearchValues<char> InvalidFileNameChars =
        SearchValues.Create([.. Path.GetInvalidFileNameChars(), '/', '\\']);

    /// <summary>
    /// Extracts every blob reachable from <paramref name="commitHash"/> in the git
    /// repository containing <paramref name="repoPathHint"/> into a new temporary
    /// directory. A blob whose path has a smudge filter driver (e.g. Git LFS) or a
    /// <c>working-tree-encoding</c> attribute is dumped with those conversions applied, as a
    /// checkout would write it, so its content matches the working tree rather than the
    /// stored form (an LFS pointer, or UTF-8 text); this runs the filter, which for Git LFS
    /// may download objects not yet in the local cache. A blob whose filter fails is
    /// skipped. Other blobs are dumped as stored, without end-of-line conversion, since that
    /// doesn't change line counts.
    /// </summary>
    /// <param name="repoPathHint">A path inside the git repository to query.</param>
    /// <param name="commitHash">The commit, tag, or tree-ish to extract.</param>
    /// <param name="cancellationToken">A token to cancel the extraction.</param>
    /// <returns>The extracted snapshot. The caller must dispose it to clean up.</returns>
    /// <exception cref="GitSnapshotException">
    /// <c>git</c> is not on <c>PATH</c>, <paramref name="repoPathHint"/> is not inside a
    /// git repository, or <paramref name="commitHash"/> does not resolve to a tree.
    /// </exception>
    public GitSnapshot Extract(string repoPathHint, string commitHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repoPathHint);
        ArgumentNullException.ThrowIfNull(commitHash);

        // git needs a directory to run in: a file hint runs from its parent directory and
        // scopes RelativePrefix to the file itself. A missing path is reported as such up
        // front, since starting git in it would otherwise fail with a confusing process-start
        // error (on Unix, the same "not found" errno as git itself missing from PATH).
        string hintDirectory;
        string? hintFileName = null;
        if (Directory.Exists(repoPathHint))
        {
            hintDirectory = repoPathHint;
        }
        else if (File.Exists(repoPathHint))
        {
            var fullHint = Path.GetFullPath(repoPathHint);
            hintDirectory = Path.GetDirectoryName(fullHint) ?? fullHint;
            hintFileName = Path.GetFileName(fullHint);
        }
        else
        {
            throw new GitSnapshotException($"Path not found: {repoPathHint}");
        }

        var repoRoot = RunGit(hintDirectory, ["rev-parse", "--show-toplevel"]).Trim();
        // A "--" separator would stop git from resolving this as a revision at all (rev-parse
        // then treats it as a pathspec, and "--verify" fails outright), so reject a leading
        // "-" up front instead: no valid commit-ish (SHA, branch, or tag) can start with one
        // per git-check-ref-format, so this only ever rejects something that would otherwise
        // be misparsed as an option (e.g. "--upload-pack=...") by the "rev-parse" call below.
        if (commitHash.Length > 0 && commitHash[0] == '-')
        {
            throw new GitSnapshotException($"Invalid commit-ish: {commitHash}");
        }

        // Resolve the revision on its own before peeling it to a tree: appending "^{tree}"
        // directly would make git read a "rev:path" tree-ish (e.g. "HEAD:src") as the path
        // "src^{tree}". The resolved object is a full hash, so the peel below is unambiguous.
        var objectHash = RunGit(repoRoot, ["rev-parse", "--verify", "--quiet", commitHash]).Trim();
        var treeHash = RunGit(repoRoot, ["rev-parse", "--verify", "--quiet", $"{objectHash}^{{tree}}"]).Trim();
        // Trailing slash trimmed so a subdirectory match is "prefix" or "prefix/...", never
        // "prefix/" (an empty result means repoPathHint was the repo root itself).
        var relativePrefix = RunGit(hintDirectory, ["rev-parse", "--show-prefix"]).Trim().TrimEnd('/');
        var treePrefix = TreePathPrefix(commitHash, relativePrefix);
        if (hintFileName is not null)
        {
            relativePrefix = relativePrefix.Length == 0 ? hintFileName : relativePrefix + "/" + hintFileName;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "sloc-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (blobsToExtract, skipped) = ListTree(repoRoot, treeHash);

            // On a case-insensitive filesystem a file hint can name the file in a different
            // case than git recorded (e.g. "fileanalyzer.cs" for "FileAnalyzer.cs"); use git's
            // spelling so callers can match RelativePrefix against git paths exactly.
            if (hintFileName is not null
                && !blobsToExtract.Exists(blob => blob.GitPath == relativePrefix)
                && blobsToExtract.Find(blob => string.Equals(blob.GitPath, relativePrefix, StringComparison.OrdinalIgnoreCase))
                    is { GitPath: { } trackedPath })
            {
                relativePrefix = trackedPath;
            }

            var filtered = FindFilteredBlobs(repoRoot, treePrefix, blobsToExtract, cancellationToken);
            var files = ExtractBlobs(repoRoot, tempRoot, treePrefix, blobsToExtract, filtered, skipped, cancellationToken);
            return new GitSnapshot(tempRoot, relativePrefix, files, skipped);
        }
        catch
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup on the failure path.
            }

            throw;
        }
    }

    /// <summary>
    /// Makes a git tree entry name safe to use as a single file name on this platform while
    /// resolving to the same language as the original: characters invalid in a file name
    /// (including path separators) become <c>_</c>; a trailing dot or space (which Windows
    /// would silently strip, turning e.g. <c>gen.c.</c> into a C file) gets a <c>_</c>
    /// appended instead of being removed, which also covers <c>.</c> and <c>..</c>; and
    /// Windows reserved device names (<c>CON</c>, <c>aux.c</c>, …) are prefixed with <c>_</c>
    /// so they don't open a device. A name longer than <see cref="MaxFileNameBytes"/> is cut
    /// down to its tail (which holds the extension and any language suffix such as
    /// <c>.designer.cs</c>) behind a <c>_</c>. The result never contains a path separator and
    /// is never <c>.</c>/<c>..</c>, so combining it with a directory can't escape that directory.
    /// Returns <paramref name="name"/> itself (no allocation) when it is already safe.
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        if (IsSafeFileName(name))
        {
            return name;
        }

        if (name.Length == 0)
        {
            return "_";
        }

        var sanitized = name;
        if (name.AsSpan().ContainsAny(InvalidFileNameChars))
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (InvalidFileNameChars.Contains(chars[i]))
                {
                    chars[i] = '_';
                }
            }

            sanitized = new string(chars);
        }

        if (sanitized[^1] is '.' or ' ')
        {
            sanitized += "_";
        }

        if (OperatingSystem.IsWindows() && IsWindowsReservedName(Stem(sanitized)))
        {
            sanitized = "_" + sanitized;
        }

        // Checked last, since the fixes above can each lengthen the name by one character.
        if (Encoding.UTF8.GetByteCount(sanitized) > MaxFileNameBytes)
        {
            sanitized = "_" + Utf8Tail(sanitized, MaxFileNameBytes - 1);
        }

        return sanitized;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be used as a file name exactly as-is, i.e.
    /// <see cref="SanitizeFileName"/> would return it unchanged.
    /// </summary>
    internal static bool IsSafeFileName(ReadOnlySpan<char> name) =>
        name.Length > 0
        && !name.ContainsAny(InvalidFileNameChars)
        && name[^1] is not ('.' or ' ')
        && !(OperatingSystem.IsWindows() && IsWindowsReservedName(Stem(name)))
        && Encoding.UTF8.GetByteCount(name) <= MaxFileNameBytes;

    /// <summary>
    /// The part of <paramref name="name"/> Windows matches against reserved device names: up
    /// to the first dot, ignoring trailing spaces.
    /// </summary>
    private static ReadOnlySpan<char> Stem(ReadOnlySpan<char> name)
    {
        var dot = name.IndexOf('.');
        return (dot < 0 ? name : name[..dot]).TrimEnd(' ');
    }

    /// <summary>
    /// Returns the longest suffix of <paramref name="name"/> whose UTF-8 encoding fits in
    /// <paramref name="maxBytes"/>, never splitting a surrogate pair.
    /// </summary>
    private static string Utf8Tail(string name, int maxBytes)
    {
        var start = name.Length;
        var bytes = 0;
        while (start > 0)
        {
            var width = start >= 2 && char.IsSurrogatePair(name[start - 2], name[start - 1]) ? 2 : 1;
            var runeBytes = width == 2 ? 4 : name[start - 1] switch
            {
                < '\u0080' => 1,
                < 'ࠀ' => 2,
                _ => 3 // Includes a lone surrogate, which UTF-8 encodes as U+FFFD.
            };

            if (bytes + runeBytes > maxBytes)
            {
                break;
            }

            bytes += runeBytes;
            start -= width;
        }

        return name[start..];
    }

    private static bool IsWindowsReservedName(ReadOnlySpan<char> stem) => stem.Length switch
    {
        3 => stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase),
        // COM0-9 and LPT0-9, including the superscript-digit variants Windows also reserves.
        4 => (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'),
        6 => stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase),
        7 => stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static void CopyExactly(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("git cat-file --batch closed the stream early.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static string DescribeFailure(string[] arguments, string stderr)
    {
        var message = stderr.Trim();
        return arguments[0] switch
        {
            "rev-parse" when arguments.Contains("--show-toplevel") =>
                "not a git repository (or any of the parent directories).",
            "rev-parse" =>
                "the given commit-ish is not a valid commit or tree-ish in this repository.",
            _ => string.IsNullOrEmpty(message) ? $"git {string.Join(' ', arguments)} failed." : message
        };
    }

    /// <summary>
    /// Returns the repository-relative directory the tree named by <paramref name="commitHash"/>
    /// sits at: empty for a commit or root tree, or the path of a <c>rev:path</c> tree-ish,
    /// where a path starting with <c>./</c> or <c>../</c> is relative to
    /// <paramref name="currentPrefix"/>, as git resolves it. Tree entry paths are relative to
    /// that tree, but attributes are looked up by repository-relative path.
    /// </summary>
    /// <param name="commitHash">The commit-ish or tree-ish as given by the caller.</param>
    /// <param name="currentPrefix">The repository-relative directory git runs in, without a trailing <c>/</c>.</param>
    internal static string TreePathPrefix(string commitHash, string currentPrefix)
    {
        var colon = commitHash.IndexOf(':');
        if (colon < 0)
        {
            return string.Empty;
        }

        var path = commitHash[(colon + 1)..];
        var segments = new List<string>();
        if (path is "." or ".." || path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal))
        {
            segments.AddRange(currentPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Returns the repository-relative path of a tree entry listed below <paramref name="treePrefix"/>.
    /// </summary>
    /// <param name="treePrefix">The tree's repository-relative directory (see <see cref="TreePathPrefix"/>).</param>
    /// <param name="gitPath">The entry's path relative to the tree.</param>
    private static string RepositoryPath(string treePrefix, string gitPath) =>
        treePrefix.Length == 0 ? gitPath : treePrefix + "/" + gitPath;

    /// <summary>
    /// Returns the names of the filter drivers that define a <c>smudge</c> or <c>process</c>
    /// command in the repository's effective git config. A <c>filter</c> attribute naming any
    /// other driver leaves checked-out content unchanged, as git does.
    /// </summary>
    /// <param name="repoRoot">The repository's working-tree root.</param>
    private static HashSet<string> ReadSmudgeDrivers(string repoRoot)
    {
        string[] arguments = ["config", "-z", "--get-regexp", @"^filter\..+\.(smudge|process)$"];
        var (stdout, stderr, exitCode) = RunGitCapture(repoRoot, arguments);
        var drivers = new HashSet<string>(StringComparer.Ordinal);

        // Exit code 1 means no key matched.
        if (exitCode == 1)
        {
            return drivers;
        }

        if (exitCode != 0)
        {
            throw new GitSnapshotException(DescribeFailure(arguments, stderr));
        }

        // Each record is "<key>\n<value>"; the key's section and variable name are lower-case,
        // the driver name (the subsection between them) is kept as written and may contain dots.
        using var stream = new MemoryStream(stdout);
        foreach (var record in ReadNullTerminatedRecords(stream))
        {
            var newline = record.IndexOf('\n');
            if (newline < 0 || newline == record.Length - 1)
            {
                continue;
            }

            var key = record[..newline];
            var lastDot = key.LastIndexOf('.');
            if (key.StartsWith("filter.", StringComparison.Ordinal) && lastDot > "filter.".Length)
            {
                drivers.Add(key["filter.".Length..lastDot]);
            }
        }

        return drivers;
    }

    /// <summary>
    /// Returns the indexes into <paramref name="blobs"/> of the blobs a checkout would convert
    /// in a way that can change what is counted: a <c>filter</c> attribute naming a driver with
    /// a smudge command (see <see cref="ReadSmudgeDrivers"/>), or a <c>working-tree-encoding</c>.
    /// Attributes are resolved by <c>git check-attr</c> the same way <c>git cat-file --filters</c>
    /// resolves them when the blobs are then dumped.
    /// </summary>
    /// <param name="repoRoot">The repository's working-tree root.</param>
    /// <param name="treePrefix">The tree's repository-relative directory (see <see cref="TreePathPrefix"/>).</param>
    /// <param name="blobs">The blobs to be dumped.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    private static HashSet<int> FindFilteredBlobs(
        string repoRoot,
        string treePrefix,
        List<(string Hash, string GitPath)> blobs,
        CancellationToken cancellationToken)
    {
        var filtered = new HashSet<int>();

        // A path git could never check out (one with a "." or ".." segment, which only a
        // hand-built tree can hold) has no attributes, and check-attr rejects the whole
        // request if one is outside the repository, so such paths are dumped unconverted.
        var queried = new List<int>();
        for (var i = 0; i < blobs.Count; i++)
        {
            if (IsCheckoutPath(RepositoryPath(treePrefix, blobs[i].GitPath)))
            {
                queried.Add(i);
            }
        }

        if (queried.Count == 0)
        {
            return filtered;
        }

        var drivers = ReadSmudgeDrivers(repoRoot);
        string[] arguments = ["check-attr", "-z", "--stdin", "filter", "working-tree-encoding"];
        using var process = StartGit(repoRoot, arguments, redirectInput: true);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdin = process.StandardInput.BaseStream;

        // Written on a background thread while this thread reads, for the same reason as in
        // ExtractBlobs: the listing can exceed the pipe buffers in both directions.
        var writerTask = Task.Run(() =>
        {
            try
            {
                foreach (var index in queried)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = Encoding.UTF8.GetBytes(RepositoryPath(treePrefix, blobs[index].GitPath) + "\0");
                    stdin.Write(request, 0, request.Length);
                }

                stdin.Flush();
            }
            catch (IOException)
            {
                // git exited early; its exit code is checked below.
            }
            finally
            {
                stdin.Dispose();
            }
        }, cancellationToken);

        // The output is "<path>\0<attribute>\0<value>\0" for each requested attribute of each
        // path, in request order, so the n-th pair of triples belongs to the n-th queried blob. A value
        // can be empty (e.g. "filter="), so empty records must be kept to stay aligned.
        using (var stdout = new BufferedStream(process.StandardOutput.BaseStream, 65536))
        {
            var fields = new string[3];
            var field = 0;
            var triple = 0;
            foreach (var record in ReadNullTerminatedRecords(stdout, keepEmpty: true))
            {
                fields[field++] = record;
                if (field < fields.Length)
                {
                    continue;
                }

                field = 0;
                var value = fields[2];
                var converts = fields[1] == "filter"
                    ? drivers.Contains(value)
                    : value is not ("unspecified" or "unset" or "set" or "");
                if (converts)
                {
                    filtered.Add(queried[triple / 2]);
                }

                triple++;
            }
        }

        writerTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new GitSnapshotException(DescribeFailure(arguments, stderr));
        }

        return filtered;
    }

    /// <summary>
    /// The characters that separate path segments in a checkout: <c>/</c>, plus <c>\</c> on
    /// Windows, where git treats either as a directory separator (elsewhere a backslash is an
    /// ordinary file name character).
    /// </summary>
    private static readonly char[] CheckoutPathSeparators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    /// <summary>
    /// Returns whether <paramref name="repositoryPath"/> is a path a checkout could write:
    /// one with no empty, <c>.</c>, or <c>..</c> segment, split on
    /// <see cref="CheckoutPathSeparators"/>.
    /// </summary>
    /// <param name="repositoryPath">A repository-relative tree entry path.</param>
    /// <returns><see langword="true"/> if a checkout could write the path.</returns>
    internal static bool IsCheckoutPath(string repositoryPath) =>
        repositoryPath.Split(CheckoutPathSeparators).All(segment => segment is not ("" or "." or ".."));

    /// <summary>
    /// Dumps one blob with the checkout conversions for its path applied, via
    /// <c>git cat-file --filters</c>. Unlike <c>cat-file --batch --filters</c>, whose header
    /// reports the unconverted size, this needs no size to know where the content ends.
    /// </summary>
    /// <param name="repoRoot">The repository's working-tree root.</param>
    /// <param name="hash">The blob's object hash.</param>
    /// <param name="gitPath">The blob's path relative to the extracted tree.</param>
    /// <param name="repositoryPath">The blob's repository-relative path, which selects the conversions.</param>
    /// <param name="allocator">Allocates the temporary file to dump into.</param>
    /// <param name="skipped">Receives the blob, with git's error, when the conversion fails.</param>
    /// <param name="cancellationToken">A token to cancel the dump.</param>
    /// <returns>The dumped file, or <see langword="null"/> when the conversion failed.</returns>
    private static GitSnapshotFile? ExtractFilteredBlob(
        string repoRoot,
        string hash,
        string gitPath,
        string repositoryPath,
        SnapshotFileAllocator allocator,
        List<Models.SkippedEntry> skipped,
        CancellationToken cancellationToken)
    {
        using var process = StartGit(repoRoot, ["cat-file", "--filters", $"--path={repositoryPath}", hash], redirectInput: false);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        string tempPath;
        using (var fileStream = allocator.Create(gitPath, out tempPath))
        {
            try
            {
                process.StandardOutput.BaseStream.CopyToAsync(fileStream, cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // A filter such as Git LFS can block on a download; don't leave it running.
                process.Kill(entireProcessTree: true);
                throw;
            }
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return new GitSnapshotFile(tempPath, gitPath);
        }

        File.Delete(tempPath);
        var message = stderr.Trim().Split('\n', 2)[0].Trim();
        skipped.Add(new Models.SkippedEntry(gitPath, $"git checkout filter error: {message}"));
        return null;
    }

    private static List<GitSnapshotFile> ExtractBlobs(
        string repoRoot,
        string tempRoot,
        string treePrefix,
        List<(string Hash, string GitPath)> blobs,
        HashSet<int> filtered,
        List<Models.SkippedEntry> skipped,
        CancellationToken cancellationToken)
    {
        var files = new List<GitSnapshotFile>(blobs.Count);
        if (blobs.Count == 0)
        {
            return files;
        }

        var allocator = new SnapshotFileAllocator(tempRoot);

        using var process = StartGit(repoRoot, ["cat-file", "--batch"], redirectInput: true);
        // Nothing here reports git's stderr, but it must still be drained: once the pipe
        // buffer fills, git blocks writing to it and stops producing stdout, which would
        // hang the read loop below.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdin = process.StandardInput.BaseStream;
        var stdout = new BufferedStream(process.StandardOutput.BaseStream, 65536);

        // Write every request on a background thread while this thread reads responses,
        // instead of alternating one write then one blocking read per blob. That previous
        // request/response ping-pong left git idle between blobs; pipelining lets it start
        // producing the next blob's header while this thread is still copying the current
        // one's content. Writing and reading concurrently (rather than writing everything
        // up front) also avoids a deadlock if the requests exceed the stdin pipe buffer
        // while nobody is draining stdout yet.
        var writerTask = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < blobs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (filtered.Contains(i))
                    {
                        // Dumped separately, with its checkout conversions applied.
                        continue;
                    }

                    var request = Encoding.ASCII.GetBytes(blobs[i].Hash + "\n");
                    stdin.Write(request, 0, request.Length);
                }

                stdin.Flush();
            }
            catch (IOException)
            {
                // The read loop below already detects (or will detect) that the batch
                // process closed its stdout early; the matching stdin write failure is
                // reported there, so it is safe to swallow here.
            }
            finally
            {
                stdin.Dispose();
            }
        }, cancellationToken);

        for (var i = 0; i < blobs.Count; i++)
        {
            var gitPath = blobs[i].GitPath;
            cancellationToken.ThrowIfCancellationRequested();

            if (filtered.Contains(i))
            {
                if (ExtractFilteredBlob(repoRoot, blobs[i].Hash, gitPath, RepositoryPath(treePrefix, gitPath), allocator, skipped, cancellationToken)
                    is { } filteredFile)
                {
                    files.Add(filteredFile);
                }

                continue;
            }

            string header;
            try
            {
                header = ReadLine(stdout);
            }
            catch (EndOfStreamException)
            {
                // The batch stream closed early; every blob from here on, including this
                // one, can no longer be read, so all of them must be reported as skipped
                // rather than silently dropped from both Files and Skipped.
                for (var j = i; j < blobs.Count; j++)
                {
                    skipped.Add(new Models.SkippedEntry(
                        blobs[j].GitPath, "git cat-file error: unexpected end of output"));
                }

                break;
            }

            // "<hash> <type> <size>" on success, "<hash> missing" when the blob cannot be read.
            var headerParts = header.Split(' ');
            if (headerParts.Length != 3 || !long.TryParse(headerParts[2], out var size))
            {
                skipped.Add(new Models.SkippedEntry(gitPath, $"git cat-file error: {header}"));
                continue;
            }

            string tempPath;
            using (var fileStream = allocator.Create(gitPath, out tempPath))
            {
                CopyExactly(stdout, fileStream, size, cancellationToken);
            }

            // Consume the trailing newline separator after the blob content.
            _ = stdout.ReadByte();

            files.Add(new GitSnapshotFile(tempPath, gitPath));
        }

        writerTask.GetAwaiter().GetResult();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
        }

        _ = stderrTask.GetAwaiter().GetResult();
        return files;
    }

    private static (List<(string Hash, string GitPath)> Blobs, List<Models.SkippedEntry> Skipped) ListTree(
                    string repoRoot, string treeHash)
    {
        string[] arguments = ["ls-tree", "-r", "-z", treeHash];
        using var process = StartGit(repoRoot, arguments, redirectInput: false);
        var stderrTask = process.StandardError.ReadToEndAsync();
        var blobs = new List<(string Hash, string GitPath)>();
        var skipped = new List<Models.SkippedEntry>();

        // Parse records as they arrive instead of buffering the whole listing into one
        // byte array and one decoded string first; a monorepo's tree can list millions of
        // entries, and that buffering was an extra full-size allocation for no benefit.
        using var stdout = new BufferedStream(process.StandardOutput.BaseStream, 65536);
        foreach (var record in ReadNullTerminatedRecords(stdout))
        {
            // Each record is: "<mode> <type> <hash>\t<path>"
            var tabIndex = record.IndexOf('\t');
            if (tabIndex < 0)
            {
                continue;
            }

            var header = record[..tabIndex];
            var gitPath = record[(tabIndex + 1)..];
            var parts = header.Split(' ', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var mode = parts[0];
            var type = parts[1];
            var hash = parts[2];

            if (mode == "120000")
            {
                skipped.Add(new Models.SkippedEntry(gitPath, "symlink"));
                continue;
            }

            if (type == "commit" || mode == "160000")
            {
                skipped.Add(new Models.SkippedEntry(gitPath, "submodule"));
                continue;
            }

            blobs.Add((hash, gitPath));
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new GitSnapshotException(DescribeFailure(arguments, stderr));
        }

        return (blobs, skipped);
    }

    /// <summary>
    /// Reads records separated by NUL bytes from <paramref name="stream"/>, decoding each
    /// as UTF-8 as soon as its terminator is seen. Splitting on 0x00 is always safe for
    /// UTF-8 text since that byte never appears within or between multi-byte sequences.
    /// </summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="keepEmpty">Whether to yield empty records between two NUL bytes instead of skipping them.</param>
    private static IEnumerable<string> ReadNullTerminatedRecords(Stream stream, bool keepEmpty = false)
    {
        var buffer = new byte[4096];
        var length = 0;
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == 0)
            {
                if (length > 0 || keepEmpty)
                {
                    yield return Encoding.UTF8.GetString(buffer, 0, length);
                    length = 0;
                }

                continue;
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            buffer[length++] = (byte)b;
        }

        if (length > 0)
        {
            yield return Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    private static string ReadLine(Stream stream)
    {
        var buffer = new byte[128];
        var length = 0;
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            buffer[length++] = (byte)b;
        }

        throw new EndOfStreamException();
    }

    private static string RunGit(string workingDirectory, string[] arguments)
    {
        var output = RunGitRaw(workingDirectory, arguments);
        return Encoding.UTF8.GetString(output);
    }

    private static byte[] RunGitRaw(string workingDirectory, string[] arguments)
    {
        var (stdout, stderr, exitCode) = RunGitCapture(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new GitSnapshotException(DescribeFailure(arguments, stderr));
        }

        return stdout;
    }

    /// <summary>
    /// Runs git to completion, returning its output and exit code without interpreting them.
    /// </summary>
    /// <param name="workingDirectory">The directory to run git in.</param>
    /// <param name="arguments">The git arguments.</param>
    private static (byte[] Stdout, string Stderr, int ExitCode) RunGitCapture(string workingDirectory, string[] arguments)
    {
        using var process = StartGit(workingDirectory, arguments, redirectInput: false);
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return (stdout.ToArray(), stderr, process.ExitCode);
    }

    private static System.Diagnostics.Process StartGit(string workingDirectory, string[] arguments, bool redirectInput)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return System.Diagnostics.Process.Start(startInfo)
                ?? throw new GitSnapshotException("failed to start the git process.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Win32 error 2 is ERROR_FILE_NOT_FOUND, i.e. the "git" executable itself
            // could not be located on PATH. Any other native error (e.g. a bad working
            // directory) is a different failure and should not be misreported as that.
            throw new GitSnapshotException(
                ex.NativeErrorCode == 2
                    ? "git executable not found on PATH."
                    : $"failed to start the git process: {ex.Message}");
        }
    }
}

/// <summary>
/// Chooses and creates the temporary file each blob of a <see cref="GitSnapshotExtractor"/>
/// extraction is dumped into: <c>&lt;tempRoot&gt;/&lt;chunk&gt;/&lt;slot&gt;/&lt;name&gt;</c>,
/// where <c>name</c> is the entry's <see cref="GitSnapshotExtractor.SanitizeFileName">sanitized</see>
/// file name (all that language detection looks at), <c>chunk</c> groups every
/// <see cref="FilesPerChunk"/> consecutive blobs so no directory grows unboundedly large,
/// and <c>slot</c> is how many earlier blobs in that chunk had the same name, compared
/// case-insensitively. Not safe for concurrent use.
/// </summary>
/// <remarks>
/// git's directory structure is deliberately not mirrored on disk. Tree entry names are
/// untrusted and git allows characters in them (e.g. <c>\</c>, <c>:</c>) that another
/// platform treats as path syntax, which could otherwise place a file outside the temp root;
/// and paths that differ only by case (<c>Foo.c</c>/<c>foo.c</c>, or a file <c>a</c> beside a
/// directory <c>A/</c>) would collide on a case-insensitive filesystem. Results are mapped
/// back to the git path via <see cref="GitSnapshotFile"/>, so the temp layout is never
/// user-visible.
/// <para>
/// A filesystem can also equate names that compare different here (Unicode normalization on
/// macOS, NTFS 8.3 short-name aliases), so a create that finds the name already taken moves
/// on to the next slot. Any other I/O error is a real failure (e.g. the temp directory isn't
/// writable) and propagates, failing the extraction rather than silently dropping files
/// from the counts.
/// </para>
/// </remarks>
internal sealed class SnapshotFileAllocator
{
    /// <summary>
    /// How many consecutive blobs share a chunk directory, bounding how many files any one
    /// directory holds.
    /// </summary>
    internal const int FilesPerChunk = 1024;

    private readonly Dictionary<string, int> _nextSlotByName = new(StringComparer.OrdinalIgnoreCase);
    // Lets an already-safe name (the common case) be looked up straight from its git path,
    // allocating a string key only the first time the name is seen in a chunk.
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _nextSlotByNameSpan;
    // Paths of this chunk's slot directories created so far; slot N is always created before
    // slot N + 1, so this doubles as the "already created" check.
    private readonly List<string> _slotDirectories = [];
    private readonly string _tempRoot;
    private int _allocated;
    private int _chunk = -1;
    private string _chunkDirectory = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotFileAllocator"/> class.
    /// </summary>
    /// <param name="tempRoot">The (existing) directory to create chunk directories under.</param>
    public SnapshotFileAllocator(string tempRoot)
    {
        _tempRoot = tempRoot;
        _nextSlotByNameSpan = _nextSlotByName.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// Creates a new, empty file for the blob at <paramref name="gitPath"/>.
    /// </summary>
    /// <param name="gitPath">The blob's forward-slash, repo-root-relative git path.</param>
    /// <param name="tempPath">Receives the full path of the created file.</param>
    /// <returns>A writable stream over the created file; the caller must dispose it.</returns>
    public FileStream Create(string gitPath, out string tempPath)
    {
        var chunk = _allocated++ / FilesPerChunk;
        if (chunk != _chunk)
        {
            // Names only need to be unique within a chunk, so the map stays small.
            _chunk = chunk;
            _chunkDirectory = Path.Combine(_tempRoot, chunk.ToString(CultureInfo.InvariantCulture));
            _nextSlotByName.Clear();
            _slotDirectories.Clear();
        }

        var leaf = gitPath.AsSpan(gitPath.LastIndexOf('/') + 1);
        var name = GitSnapshotExtractor.IsSafeFileName(leaf)
            ? leaf
            : GitSnapshotExtractor.SanitizeFileName(leaf.ToString()).AsSpan();

        while (true)
        {
            var slot = CollectionsMarshal.GetValueRefOrAddDefault(_nextSlotByNameSpan, name, out _)++;
            if (slot == _slotDirectories.Count)
            {
                var created = Path.Combine(_chunkDirectory, slot.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(created);
                _slotDirectories.Add(created);
            }

            tempPath = Path.Join(_slotDirectories[slot], name);
            try
            {
                return new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(tempPath))
            {
                // Taken under an alias of this name; try the next slot.
            }
        }
    }
}
