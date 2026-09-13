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
/// <param name="RepoRoot">
/// The absolute path of the git repository's working-tree root, as resolved by
/// <c>git rev-parse --show-toplevel</c>. Lets a caller that was given a subdirectory of the
/// repo compute that subdirectory's git-relative prefix and filter <see cref="Files"/> to it.
/// </param>
/// <param name="Files">The dumped files, paired with their git-relative paths.</param>
/// <param name="Skipped">
/// Tree entries that were not dumped (symlinks, submodules/gitlinks, or blobs that
/// could not be read), together with the reason.
/// </param>
public sealed record GitSnapshot(
    string TempRoot,
    string RepoRoot,
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

        var repoRoot = RunGit(repoPathHint, ["rev-parse", "--show-toplevel"]).Trim();
        var treeHash = RunGit(repoRoot, ["rev-parse", "--verify", "--quiet", $"{commitHash}^{{tree}}"]).Trim();

        var tempRoot = Path.Combine(Path.GetTempPath(), "sloc-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (blobsToExtract, skipped) = ListTree(repoRoot, treeHash);
            var files = ExtractBlobs(repoRoot, tempRoot, blobsToExtract, skipped, cancellationToken);
            return new GitSnapshot(tempRoot, repoRoot, files, skipped);
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

        using var process = StartGit(repoRoot, ["cat-file", "--batch"], redirectInput: true);
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

            var tempPath = Path.Combine(tempRoot, gitPath.Replace('/', Path.DirectorySeparatorChar));
            var parentDir = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
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

        return files;
    }

    private static (List<(string Hash, string GitPath)> Blobs, List<Models.SkippedEntry> Skipped) ListTree(
                    string repoRoot, string treeHash)
    {
        string[] arguments = ["ls-tree", "-r", "-z", treeHash];
        using var process = StartGit(repoRoot, arguments, redirectInput: false);
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

        var stderr = process.StandardError.ReadToEnd();
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
        using var stdout = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        var stderr = process.StandardError.ReadToEnd();
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