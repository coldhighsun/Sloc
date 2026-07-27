using System.Diagnostics;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitSnapshotExtractor"/>. Each test creates a real
/// temporary git repository (there is no git library to mock against) and removes it on
/// disposal.
/// </summary>
public sealed class GitSnapshotExtractorTests : IDisposable
{
    private readonly GitSnapshotExtractor _extractor = new();
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="GitSnapshotExtractorTests"/>, creating a
    /// unique temporary git repository for the test.
    /// </summary>
    public GitSnapshotExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-gittest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        RunGit(_root, "init", "-q");
        RunGit(_root, "config", "user.email", "test@example.com");
        RunGit(_root, "config", "user.name", "Test");
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
    /// Verifies that dumped file content is byte-identical to the original, including
    /// embedded NUL bytes, to guard against accidental text decoding of git's output.
    /// </summary>
    [Fact]
    public void Extract_BinaryContent_IsDumpedByteForByte()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFF, (byte)'a', 0x00, (byte)'b' };
        File.WriteAllBytes(Path.Combine(_root, "binary.dat"), bytes);
        Commit("first");

        using var snapshot = _extractor.Extract(_root, "HEAD", TestContext.Current.CancellationToken);

        var file = Assert.Single(snapshot.Files);
        Assert.Equal(bytes, File.ReadAllBytes(file.TempPath));
    }

    /// <summary>
    /// Verifies that extracting HEAD reproduces the committed working-tree content.
    /// </summary>
    [Fact]
    public void Extract_Head_MatchesWorkingTreeContent()
    {
        Write("a.cs", "// hello");
        Write("sub/b.py", "print(1)");
        Commit("first");

        using var snapshot = _extractor.Extract(_root, "HEAD", TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Files.Count);
        Assert.Empty(snapshot.Skipped);

        var byGitPath = snapshot.Files.ToDictionary(f => f.GitPath, f => f.TempPath);
        Assert.Equal("// hello", File.ReadAllText(byGitPath["a.cs"]));
        Assert.Equal("print(1)", File.ReadAllText(byGitPath["sub/b.py"]));
    }

    /// <summary>
    /// Verifies that an invalid commit-ish throws <see cref="GitSnapshotException"/>.
    /// </summary>
    [Fact]
    public void Extract_InvalidHash_ThrowsGitSnapshotException()
    {
        Write("a.cs", "// hello");
        Commit("first");

        Assert.Throws<GitSnapshotException>(() => _extractor.Extract(_root, "not-a-real-hash", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that a path outside any git repository throws <see cref="GitSnapshotException"/>.
    /// </summary>
    [Fact]
    public void Extract_NotAGitRepo_ThrowsGitSnapshotException()
    {
        var plainDir = Path.Combine(Path.GetTempPath(), "sloc-notgit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plainDir);
        try
        {
            Assert.Throws<GitSnapshotException>(() => _extractor.Extract(plainDir, "HEAD", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(plainDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that extracting an older commit reflects the file state at that commit,
    /// not the current working tree.
    /// </summary>
    [Fact]
    public void Extract_OlderCommit_ReflectsFileStateAtThatCommit()
    {
        Write("keep.cs", "// v1");
        Write("removed-later.cs", "// will be deleted");
        Commit("first");
        var firstCommit = RunGit(_root, "rev-parse", "HEAD").Trim();

        Write("keep.cs", "// v2");
        File.Delete(Path.Combine(_root, "removed-later.cs"));
        Commit("second");

        using var snapshot = _extractor.Extract(_root, firstCommit, TestContext.Current.CancellationToken);

        var byGitPath = snapshot.Files.ToDictionary(f => f.GitPath, f => f.TempPath);
        Assert.True(byGitPath.ContainsKey("removed-later.cs"));
        Assert.Equal("// v1", File.ReadAllText(byGitPath["keep.cs"]));
    }

    /// <summary>
    /// Verifies that a git symlink tree entry is skipped rather than dumped as a file.
    /// </summary>
    [Fact]
    public void Extract_SymlinkEntry_IsSkipped()
    {
        Write("target.txt", "hello");
        Commit("first");

        var blobHash = RunGitWithFileInput(_root, ["hash-object", "-w", "--stdin"], "target.txt").Trim();
        RunGit(_root, "update-index", "--add", "--cacheinfo", $"120000,{blobHash},link.txt");
        RunGit(_root, "commit", "-q", "-m", "add symlink");

        using var snapshot = _extractor.Extract(_root, "HEAD", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(snapshot.Files, f => f.GitPath == "link.txt");
        Assert.Contains(snapshot.Skipped, s => s.Path == "link.txt" && s.Reason == "symlink");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
        => RunGit(workingDirectory, arguments, input: null);

    private static string RunGit(string workingDirectory, string[] arguments, string? input)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");

        if (input is not null)
        {
            var inputPath = Path.Combine(workingDirectory, input);
            using var inputStream = File.OpenRead(inputPath);
            inputStream.CopyTo(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }

        return stdout;
    }

    private static string RunGitWithFileInput(string workingDirectory, string[] arguments, string inputRelativePath)
        => RunGit(workingDirectory, arguments, inputRelativePath);

    private void Commit(string message)
    {
        RunGit(_root, "add", "-A");
        RunGit(_root, "commit", "-q", "-m", message);
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }
}