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
    /// directory.
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

            var files = ExtractBlobs(repoRoot, tempRoot, blobsToExtract, skipped, cancellationToken);
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

    private static List<GitSnapshotFile> ExtractBlobs(
        string repoRoot,
        string tempRoot,
        List<(string Hash, string GitPath)> blobs,
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
                foreach (var (hash, _) in blobs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = Encoding.ASCII.GetBytes(hash + "\n");
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
    private static IEnumerable<string> ReadNullTerminatedRecords(Stream stream)
    {
        var buffer = new byte[4096];
        var length = 0;
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            if (b == 0)
            {
                if (length > 0)
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
        using var process = StartGit(workingDirectory, arguments, redirectInput: false);
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new GitSnapshotException(DescribeFailure(arguments, stderr));
        }

        return stdout.ToArray();
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
