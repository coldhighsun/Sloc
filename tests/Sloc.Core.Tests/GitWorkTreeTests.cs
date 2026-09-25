using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="GitWorkTree"/>. Each test builds a small on-disk
/// directory tree in a temporary location and removes it on disposal.
/// </summary>
public sealed class GitWorkTreeTests : IDisposable
{
    /// <summary>
    /// The temporary root directory of the test.
    /// </summary>
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of <see cref="GitWorkTreeTests"/>, creating a unique
    /// temporary root directory for the test.
    /// </summary>
    public GitWorkTreeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sloc-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Deletes the temporary directory tree created for the test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a <c>.git</c> directory above the start directory is found, and that it
    /// is its own common directory.
    /// </summary>
    [Fact]
    public void Find_GitDirectoryAbove_ReturnsEnclosingWorkTree()
    {
        var gitDirectory = Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;

        var workTree = GitWorkTree.Find(sub);

        Assert.NotNull(workTree);
        Assert.Equal(Path.GetFullPath(_root), workTree.Root);
        Assert.Equal(gitDirectory, workTree.CommonDirectory);
        Assert.Equal("a/b", workTree.RelativeDirectory(sub));
        Assert.Equal(string.Empty, workTree.RelativeDirectory(_root));
    }

    /// <summary>
    /// Verifies that a <c>.git</c> file of a linked worktree resolves to the main
    /// repository's directory through its <c>commondir</c> file.
    /// </summary>
    [Fact]
    public void Find_GitFileWithCommonDir_ReturnsMainRepositoryDirectory()
    {
        var mainGit = Directory.CreateDirectory(Path.Combine(_root, "main", ".git")).FullName;
        var worktreeGit = Directory.CreateDirectory(Path.Combine(mainGit, "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..\n");
        var wt = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {worktreeGit}\n");

        var workTree = GitWorkTree.Find(wt);

        Assert.NotNull(workTree);
        Assert.Equal(wt, workTree.Root);
        Assert.Equal(mainGit, workTree.CommonDirectory);
        Assert.Equal(Path.Combine(mainGit, "info", "exclude"), workTree.InfoExcludePath);
    }

    /// <summary>
    /// Verifies that a <c>.git</c> file pointing nowhere stops the search instead of
    /// continuing to a repository further up.
    /// </summary>
    [Fact]
    public void Find_BrokenGitFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: missing\n");

        var workTree = GitWorkTree.Find(sub);

        Assert.Null(workTree);
    }

    /// <summary>
    /// Verifies that the directories above a scan prefix are listed from the root down,
    /// excluding the prefix itself.
    /// </summary>
    /// <param name="scanPrefix">The repository-relative scan directory.</param>
    /// <param name="expected">The expected ancestors, joined with <c>|</c>.</param>
    [Theory]
    [InlineData("", "")]
    [InlineData("a", "")]
    [InlineData("a/b/c", "|a|a/b")]
    public void AncestorDirectories_ScanPrefix_ListsRootFirst(string scanPrefix, string expected)
    {
        var ancestors = GitWorkTree.AncestorDirectories(scanPrefix).ToList();

        Assert.Equal(scanPrefix.Length == 0 ? [] : expected.Split('|'), ancestors);
    }

    /// <summary>
    /// Verifies that a directory whose <c>.git</c> directory holds <c>HEAD</c>,
    /// <c>objects</c>, and <c>refs</c> is a repository root.
    /// </summary>
    [Fact]
    public void IsRepositoryRoot_ValidGitDirectory_ReturnsTrue()
    {
        CreateRepositoryDirectory(Path.Combine(_root, ".git"));

        Assert.True(GitWorkTree.IsRepositoryRoot(_root));
    }

    /// <summary>
    /// Verifies that a stray <c>.git</c> entry (an empty directory, or a file that is not a
    /// <c>gitdir:</c> pointer to a repository directory) does not make a repository root.
    /// </summary>
    /// <param name="kind">Which kind of stray <c>.git</c> entry to create.</param>
    [Theory]
    [InlineData("empty-directory")]
    [InlineData("not-a-pointer")]
    [InlineData("dangling-pointer")]
    [InlineData("pointer-to-empty-directory")]
    public void IsRepositoryRoot_StrayDotGit_ReturnsFalse(string kind)
    {
        var dotGit = Path.Combine(_root, ".git");
        switch (kind)
        {
            case "empty-directory":
                Directory.CreateDirectory(dotGit);
                break;
            case "not-a-pointer":
                File.WriteAllText(dotGit, "not a pointer\n");
                break;
            case "dangling-pointer":
                File.WriteAllText(dotGit, "gitdir: missing\n");
                break;
            default:
                Directory.CreateDirectory(Path.Combine(_root, "empty"));
                File.WriteAllText(dotGit, "gitdir: empty\n");
                break;
        }

        Assert.False(GitWorkTree.IsRepositoryRoot(_root));
    }

    /// <summary>
    /// Verifies that a <c>.git</c> file pointing at a repository directory (a submodule's
    /// <c>modules/&lt;name&gt;</c>, or a linked worktree's directory whose <c>objects</c> and
    /// <c>refs</c> live in its common directory) makes a repository root.
    /// </summary>
    /// <param name="linkedWorktree">Whether the pointer targets a linked worktree's directory.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsRepositoryRoot_GitFilePointingAtRepository_ReturnsTrue(bool linkedWorktree)
    {
        var main = Path.Combine(_root, "main.git");
        CreateRepositoryDirectory(main);
        var target = main;
        if (linkedWorktree)
        {
            target = Path.Combine(main, "worktrees", "wt");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "HEAD"), "ref: refs/heads/wt\n");
            File.WriteAllText(Path.Combine(target, "commondir"), "../..\n");
        }

        var workTree = Directory.CreateDirectory(Path.Combine(_root, "checkout")).FullName;
        File.WriteAllText(Path.Combine(workTree, ".git"), $"gitdir: {target}\n");

        Assert.True(GitWorkTree.IsRepositoryRoot(workTree));
    }

    /// <summary>
    /// Creates a minimal repository directory at <paramref name="gitDirectory"/>: a
    /// <c>HEAD</c> file and empty <c>objects</c> and <c>refs</c> directories.
    /// </summary>
    /// <param name="gitDirectory">The directory to create.</param>
    private static void CreateRepositoryDirectory(string gitDirectory)
    {
        Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
        Directory.CreateDirectory(Path.Combine(gitDirectory, "refs"));
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/main\n");
    }
}
