using Sloc.Core.Languages;
using System.Diagnostics;
using System.Text;

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
    /// Verifies that a <c>rev:path</c> tree-ish (e.g. <c>HEAD:sub</c>) is accepted and
    /// extracts that subtree, with git paths relative to it.
    /// </summary>
    [Fact]
    public void Extract_RevPathTreeish_ExtractsThatSubtree()
    {
        Write("a.cs", "// top");
        Write("sub/b.py", "print(1)");
        Commit("first");

        using var snapshot = _extractor.Extract(_root, "HEAD:sub", TestContext.Current.CancellationToken);

        var file = Assert.Single(snapshot.Files);
        Assert.Equal("b.py", file.GitPath);
        Assert.Equal("print(1)", File.ReadAllText(file.TempPath));
    }

    /// <summary>
    /// Verifies that a <c>rev:path</c> naming a blob rather than a tree is still rejected.
    /// </summary>
    [Fact]
    public void Extract_RevPathNamingBlob_ThrowsGitSnapshotException()
    {
        Write("a.cs", "// top");
        Commit("first");

        Assert.Throws<GitSnapshotException>(() => _extractor.Extract(_root, "HEAD:a.cs", TestContext.Current.CancellationToken));
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
    /// Verifies that a commit-ish starting with "-" is rejected before being passed to
    /// <c>git rev-parse</c>, where it would otherwise be misparsed as an option (e.g.
    /// <c>--upload-pack=...</c>) instead of a revision.
    /// </summary>
    [Fact]
    public void Extract_CommitHashStartingWithDash_ThrowsGitSnapshotException()
    {
        Write("a.cs", "// hello");
        Commit("first");

        Assert.Throws<GitSnapshotException>(() => _extractor.Extract(_root, "--upload-pack=x", TestContext.Current.CancellationToken));
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
    /// Verifies that a path to a file inside the repository (rather than a directory) is
    /// accepted: git runs from the file's directory, and <see cref="GitSnapshot.RelativePrefix"/>
    /// is the file's own repo-relative path.
    /// </summary>
    [Fact]
    public void Extract_FilePath_ResolvesRepoAndUsesFileAsRelativePrefix()
    {
        Write("sub/a.cs", "// hello");
        Write("b.cs", "// other");
        Commit("first");

        using var snapshot = _extractor.Extract(Path.Combine(_root, "sub", "a.cs"), "HEAD", TestContext.Current.CancellationToken);

        Assert.Equal("sub/a.cs", snapshot.RelativePrefix);
        Assert.Equal(2, snapshot.Files.Count);
    }

    /// <summary>
    /// Verifies that on a case-insensitive filesystem, a file hint whose name differs in
    /// case from the tracked path still yields git's own spelling as
    /// <see cref="GitSnapshot.RelativePrefix"/>, so it matches the extracted file's git path.
    /// </summary>
    [Fact]
    public void Extract_FilePathWithDifferentCasing_UsesTrackedCasingAsRelativePrefix()
    {
        Write("sub/MixedCase.cs", "// hello");
        Commit("first");

        var hint = Path.Combine(_root, "sub", "mixedcase.cs");
        Assert.SkipUnless(File.Exists(hint), "Requires a case-insensitive filesystem.");

        using var snapshot = _extractor.Extract(hint, "HEAD", TestContext.Current.CancellationToken);

        Assert.Equal("sub/MixedCase.cs", snapshot.RelativePrefix);
    }

    /// <summary>
    /// Verifies that a path that doesn't exist is reported as such, rather than surfacing
    /// as a failure to start git (which, on some platforms, reads as git itself missing).
    /// </summary>
    [Fact]
    public void Extract_MissingPath_ThrowsPathNotFound()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var ex = Assert.Throws<GitSnapshotException>(() => _extractor.Extract(missing, "HEAD", TestContext.Current.CancellationToken));
        Assert.Contains("Path not found", ex.Message);
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

    /// <summary>
    /// Verifies that tree entries whose paths would resolve outside the temporary directory
    /// are never written outside <see cref="GitSnapshot.TempRoot"/>: a <c>..</c> tree entry
    /// (git lists its contents as <c>../name</c>, a traversal on every platform), and a file
    /// name containing <c>\</c> (which git allows and Windows treats as a separator).
    /// </summary>
    [Fact]
    public void Extract_EntryNameWithTraversal_StaysInsideTempRoot()
    {
        var viaDotDotTree = "sloc-escape-" + Guid.NewGuid().ToString("N") + ".c";
        var viaBackslash = "sloc-escape-" + Guid.NewGuid().ToString("N") + ".c";
        var escapedPaths = new[] { viaDotDotTree, viaBackslash }.Select(n => Path.Combine(Path.GetTempPath(), n)).ToArray();
        var dotDotTree = MakeTree((viaDotDotTree, "int x;\n"));
        var tree = MakeTreeFromEntries(
            $"040000 tree {dotDotTree}\t..\n100644 blob {HashObject("int y;\n")}\t..\\{viaBackslash}\n");

        try
        {
            using var snapshot = _extractor.Extract(_root, tree, TestContext.Current.CancellationToken);

            Assert.Equal(2, snapshot.Files.Count);
            Assert.All(escapedPaths, p => Assert.False(File.Exists(p), $"blob was written outside the temp root: {p}"));
            var tempRoot = Path.GetFullPath(snapshot.TempRoot) + Path.DirectorySeparatorChar;
            Assert.All(snapshot.Files, f => Assert.StartsWith(tempRoot, Path.GetFullPath(f.TempPath), StringComparison.Ordinal));
        }
        finally
        {
            Array.ForEach(escapedPaths, File.Delete);
        }
    }

    /// <summary>
    /// Verifies that tree entries whose paths differ only by case each keep their own
    /// content, even on a case-insensitive filesystem where they would otherwise be dumped
    /// onto the same file.
    /// </summary>
    [Fact]
    public void Extract_PathsDifferingOnlyByCase_KeepTheirOwnContent()
    {
        var tree = MakeTree(("Foo.c", "int a;\n"), ("foo.c", "// c\n"));

        using var snapshot = _extractor.Extract(_root, tree, TestContext.Current.CancellationToken);

        var byGitPath = snapshot.Files.ToDictionary(f => f.GitPath, f => f.TempPath);
        Assert.Equal(2, byGitPath.Count);
        Assert.Equal("int a;\n", File.ReadAllText(byGitPath["Foo.c"]));
        Assert.Equal("// c\n", File.ReadAllText(byGitPath["foo.c"]));
    }

    /// <summary>
    /// Verifies that a file and a directory whose names differ only by case (which collide on
    /// a case-insensitive filesystem) are both extracted instead of failing the whole run.
    /// </summary>
    [Fact]
    public void Extract_FileAndDirectoryDifferingOnlyByCase_ExtractsBoth()
    {
        var subtree = MakeTree(("x.c", "int x;\n"));
        var tree = MakeTreeFromEntries($"100644 blob {HashObject("int a;\n")}\ta\n040000 tree {subtree}\tA\n");

        using var snapshot = _extractor.Extract(_root, tree, TestContext.Current.CancellationToken);

        var byGitPath = snapshot.Files.ToDictionary(f => f.GitPath, f => f.TempPath);
        Assert.Equal(["A/x.c", "a"], byGitPath.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("int a;\n", File.ReadAllText(byGitPath["a"]));
        Assert.Equal("int x;\n", File.ReadAllText(byGitPath["A/x.c"]));
    }

    /// <summary>
    /// Verifies that tree entry names are made safe to use as a single file name while
    /// keeping the parts language detection relies on (exact special file names, extensions).
    /// </summary>
    [Theory]
    [InlineData("Program.cs", "Program.cs")]
    [InlineData("Makefile", "Makefile")]
    [InlineData(".", "._")]
    [InlineData("..", ".._")]
    [InlineData("", "_")]
    [InlineData("a.c. ", "a.c. _")]
    [InlineData("..\\evil.c", ".._evil.c")]
    public void SanitizeFileName_ProducesSafeSingleFileName(string name, string expected) =>
        Assert.Equal(expected, GitSnapshotExtractor.SanitizeFileName(name));

    /// <summary>
    /// Verifies that a name that is (or, after the other fixes, becomes) longer than the
    /// per-component file name limit is cut down to fit while keeping its tail, so it still
    /// resolves to the same language instead of failing the whole extraction.
    /// </summary>
    [Theory]
    [MemberData(nameof(TooLongNames))]
    public void SanitizeFileName_TooLong_FitsLimitAndKeepsLanguage(string name)
    {
        var sanitized = GitSnapshotExtractor.SanitizeFileName(name);

        Assert.InRange(Encoding.UTF8.GetByteCount(sanitized), 1, GitSnapshotExtractor.MaxFileNameBytes);
        Assert.False(char.IsLowSurrogate(sanitized[1]), "a surrogate pair was split");
        LanguageRegistry.TryGetByPath(name, out var original);
        LanguageRegistry.TryGetByPath(sanitized, out var kept);
        Assert.Equal(original?.Name, kept?.Name);
    }

    /// <summary>
    /// File names longer than <see cref="GitSnapshotExtractor.MaxFileNameBytes"/>, either to
    /// begin with or once <see cref="GitSnapshotExtractor.SanitizeFileName"/> lengthens them.
    /// </summary>
    public static TheoryData<string> TooLongNames() => new()
    {
        // At the limit, then pushed over it by the trailing-dot fix.
        new string('a', GitSnapshotExtractor.MaxFileNameBytes - 3) + ".c.",
        // Over the limit to begin with.
        new string('a', 300) + ".py",
        // Multi-byte: under the limit in chars but over it in UTF-8 bytes.
        new string('é', 200) + ".designer.cs",
        // Surrogate pairs (4 UTF-8 bytes each) must not be split when cutting.
        string.Concat(Enumerable.Repeat("\U0001F600", 100)) + ".rs",
    };

    /// <summary>
    /// Verifies end to end that a tree entry whose name is at the file name limit and needs
    /// lengthening is extracted rather than failing the whole run.
    /// </summary>
    [Fact]
    public void Extract_NameAtLengthLimitNeedingFix_IsExtracted()
    {
        var longName = new string('a', GitSnapshotExtractor.MaxFileNameBytes - 3) + ".c.";
        var tree = MakeTree((longName, "int x;\n"));

        using var snapshot = _extractor.Extract(_root, tree, TestContext.Current.CancellationToken);

        var file = Assert.Single(snapshot.Files);
        Assert.Equal(longName, file.GitPath);
        Assert.Equal("int x;\n", File.ReadAllText(file.TempPath));
    }

    /// <summary>
    /// Verifies that an already-safe name (the common case) is returned as-is, without
    /// allocating a copy per extracted blob.
    /// </summary>
    [Fact]
    public void SanitizeFileName_SafeName_ReturnsSameInstance()
    {
        var name = string.Concat("Program", ".cs");
        Assert.Same(name, GitSnapshotExtractor.SanitizeFileName(name));
    }

    /// <summary>
    /// Verifies that sanitizing a name never changes the language it resolves to, so
    /// <c>--git-hash</c> classifies a file the same way a working-tree scan does (e.g.
    /// <c>gen.c.</c> must not become a C file by losing its trailing dot).
    /// </summary>
    [Theory]
    [InlineData("gen.c.")]
    [InlineData("gen.c ")]
    [InlineData("a:b.py")]
    [InlineData("aux.c")]
    [InlineData("Makefile")]
    [InlineData("CMakeLists.txt")]
    [InlineData("Form1.designer.cs")]
    public void SanitizeFileName_PreservesResolvedLanguage(string name)
    {
        LanguageRegistry.TryGetByPath(name, out var original);
        LanguageRegistry.TryGetByPath(GitSnapshotExtractor.SanitizeFileName(name), out var sanitized);

        Assert.Equal(original?.Name, sanitized?.Name);
    }

    /// <summary>
    /// Verifies that when the filesystem already has a file under the chosen name (as happens
    /// when it equates two names the bucket map considers distinct, e.g. Unicode normalization
    /// variants on macOS or NTFS 8.3 aliases), the blob moves to the next bucket instead of
    /// failing or overwriting the existing file.
    /// </summary>
    [Fact]
    public void Allocator_NameAlreadyTakenOnDisk_UsesNextSlot()
    {
        var tempRoot = Path.Combine(_root, "extract");
        var aliasPath = Path.Combine(tempRoot, "0", "0", "Foo.c");
        Directory.CreateDirectory(Path.GetDirectoryName(aliasPath)!);
        File.WriteAllText(aliasPath, "existing");

        using (new SnapshotFileAllocator(tempRoot).Create("src/Foo.c", out var tempPath))
        {
            Assert.Equal(Path.Combine(tempRoot, "0", "1", "Foo.c"), tempPath);
        }

        Assert.Equal("existing", File.ReadAllText(aliasPath));
    }

    /// <summary>
    /// Verifies that blobs are spread across chunk directories of at most
    /// <see cref="SnapshotFileAllocator.FilesPerChunk"/> files each, and that same-named
    /// blobs share a chunk via separate slots, restarting at slot 0 in the next chunk.
    /// </summary>
    [Fact]
    public void Allocator_SpreadsFilesAcrossBoundedChunks()
    {
        var tempRoot = Path.Combine(_root, "extract");
        var allocator = new SnapshotFileAllocator(tempRoot);
        var paths = new List<string>();
        for (var i = 0; i < SnapshotFileAllocator.FilesPerChunk + 2; i++)
        {
            // Three blobs share the name "same.c" (two in chunk 0, one opening chunk 1) to
            // exercise slots; every other blob has a unique name.
            var gitPath = i is 0 or 1 or SnapshotFileAllocator.FilesPerChunk ? $"d{i}/same.c" : $"f{i}.c";
            using (allocator.Create(gitPath, out var tempPath))
            {
                paths.Add(tempPath);
            }
        }

        Assert.Equal(Path.Combine(tempRoot, "0", "0", "same.c"), paths[0]);
        Assert.Equal(Path.Combine(tempRoot, "0", "1", "same.c"), paths[1]);
        Assert.Equal(Path.Combine(tempRoot, "1", "0", "same.c"), paths[SnapshotFileAllocator.FilesPerChunk]);
        Assert.Equal(Path.Combine(tempRoot, "1", "0", $"f{SnapshotFileAllocator.FilesPerChunk + 1}.c"), paths[^1]);
        Assert.All(
            Directory.EnumerateDirectories(tempRoot),
            chunk => Assert.InRange(Directory.EnumerateFiles(chunk, "*", SearchOption.AllDirectories).Count(), 1, SnapshotFileAllocator.FilesPerChunk));
    }

    /// <summary>
    /// Verifies that an I/O failure other than a name collision propagates instead of the
    /// blob being silently dropped, so an unwritable temp directory fails the run rather
    /// than producing partial counts with a success exit code.
    /// </summary>
    [Fact]
    public void Allocator_OtherIoFailure_Propagates()
    {
        var tempRoot = Path.Combine(_root, "extract");
        Directory.CreateDirectory(tempRoot);
        // A file where chunk directory "0" needs to go makes creating it fail.
        File.WriteAllText(Path.Combine(tempRoot, "0"), "blocker");

        // The exact type is platform-specific: Windows throws IOException, Unix throws its
        // DirectoryNotFoundException subclass. Callers catch IOException, so either propagates.
        Assert.ThrowsAny<IOException>(() => new SnapshotFileAllocator(tempRoot).Create("a.c", out _).Dispose());
    }

    /// <summary>
    /// Verifies that Windows reserved device names are prefixed so extraction never opens a
    /// device (e.g. <c>aux.c</c>, which real repositories contain) instead of a file.
    /// </summary>
    [Theory]
    [InlineData("aux.c", "_aux.c")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.h", "_com1.h")]
    [InlineData("auxiliary.c", "auxiliary.c")]
    [InlineData("com10.h", "com10.h")]
    public void SanitizeFileName_WindowsReservedName_IsPrefixed(string name, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reserved device names only apply on Windows.");
        Assert.Equal(expected, GitSnapshotExtractor.SanitizeFileName(name));
    }

    /// <summary>
    /// Writes each (name, content) pair as a blob and returns the hash of a tree listing
    /// them, built with <c>git mktree</c> so names a working tree couldn't hold on this
    /// platform can still be committed.
    /// </summary>
    private string MakeTree(params (string Name, string Content)[] entries) =>
        MakeTreeFromEntries(string.Concat(entries.Select(e => $"100644 blob {HashObject(e.Content)}\t{e.Name}\n")));

    private string MakeTreeFromEntries(string mktreeInput)
    {
        var inputFile = "mktree-" + Guid.NewGuid().ToString("N") + ".txt";
        File.WriteAllText(Path.Combine(_root, inputFile), mktreeInput);
        return RunGitWithFileInput(_root, ["mktree"], inputFile).Trim();
    }

    private string HashObject(string content)
    {
        var inputFile = "blob-" + Guid.NewGuid().ToString("N") + ".txt";
        File.WriteAllText(Path.Combine(_root, inputFile), content);
        return RunGitWithFileInput(_root, ["hash-object", "-w", "--stdin"], inputFile).Trim();
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