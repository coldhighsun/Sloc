using System.Diagnostics;

namespace Sloc.Core.Tests;

/// <summary>
/// Defines a non-parallel test collection for tests that set process-wide git environment
/// variables, which every git child process started by other tests would otherwise inherit.
/// </summary>
[CollectionDefinition(nameof(GitEnvironmentCollection), DisableParallelization = true)]
public sealed class GitEnvironmentCollection;

/// <summary>
/// Contains tests verifying that <see cref="GitSnapshotExtractor"/> keeps draining git's
/// standard error while it reads standard output, so a git process that writes a lot of
/// diagnostics cannot block on a full stderr pipe and deadlock the extraction.
/// </summary>
[Collection(nameof(GitEnvironmentCollection))]
public sealed class GitSnapshotExtractorStderrTests : IDisposable
{
    /// <summary>
    /// The number of files committed, chosen so git's per-object trace output far exceeds a
    /// pipe buffer on every platform (4 KB on Windows, 64 KB on Linux).
    /// </summary>
    private const int FileCount = 1000;

    /// <summary>
    /// The longest an extraction may take before it is considered deadlocked.
    /// </summary>
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The temporary git repository created for the test.
    /// </summary>
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="GitSnapshotExtractorStderrTests"/>, creating a
    /// unique temporary git repository for the test.
    /// </summary>
    public GitSnapshotExtractorStderrTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-gitstderr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit("init", "-q");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
    }

    /// <summary>
    /// Deletes the temporary git repository created for the test.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // git marks object files read-only; clear that before deleting or Windows denies access.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Verifies that extraction completes when every git command writes far more to stderr
    /// than a pipe buffer holds (here, a pack-access trace line per object read).
    /// </summary>
    [Fact]
    public void Extract_GitWritesLargeStderr_CompletesWithoutDeadlock()
    {
        for (var i = 0; i < FileCount; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i}.cs"), $"int x{i} = {i};\n");
        }

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "first");
        // Pack the objects, since only reads from a pack file are traced.
        RunGit("repack", "-a", "-d", "-q");

        var previous = Environment.GetEnvironmentVariable("GIT_TRACE_PACK_ACCESS");
        Environment.SetEnvironmentVariable("GIT_TRACE_PACK_ACCESS", "2");
        try
        {
            var extraction = Task.Run(() => new GitSnapshotExtractor().Extract(_root, "HEAD"));

            Assert.True(
                extraction.Wait(ExtractTimeout, TestContext.Current.CancellationToken),
                "Extraction did not finish; git most likely blocked writing to a full stderr pipe.");
            using var snapshot = extraction.Result;
            Assert.Equal(FileCount, snapshot.Files.Count);
            Assert.Empty(snapshot.Skipped);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TRACE_PACK_ACCESS", previous);
        }
    }

    /// <summary>
    /// Runs git in the test repository, failing the test if it exits with an error.
    /// </summary>
    /// <param name="arguments">The git arguments.</param>
    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr.Result}");
        }
    }
}
