using Sloc.Core.Scanning;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="SymlinkGuard"/>.
/// </summary>
public class SymlinkGuardTests
{
    /// <summary>
    /// Verifies that a path identical to the ancestor is treated as the ancestor itself.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_SamePath_ReturnsTrue()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");

        Assert.True(SymlinkGuard.IsAncestorOrSelf(ancestor, ancestor));
    }

    /// <summary>
    /// Verifies that a path nested under the ancestor is recognized as such.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_NestedPath_ReturnsTrue()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var nested = Path.Combine(ancestor, "src", "sub");

        Assert.True(SymlinkGuard.IsAncestorOrSelf(ancestor, nested));
    }

    /// <summary>
    /// Verifies that comparison is case-insensitive.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_DifferentCasing_ReturnsTrue()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var nested = Path.Combine(ancestor, "src").ToUpperInvariant();

        Assert.True(SymlinkGuard.IsAncestorOrSelf(ancestor, nested));
    }

    /// <summary>
    /// Verifies that a sibling directory whose name merely shares the ancestor's name as
    /// a prefix is not treated as nested under it.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_PrefixWithoutSeparator_ReturnsFalse()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var sibling = ancestor + "-extra";

        Assert.False(SymlinkGuard.IsAncestorOrSelf(ancestor, sibling));
    }

    /// <summary>
    /// Verifies that an unrelated path is not treated as nested under the ancestor.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_UnrelatedPath_ReturnsFalse()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var unrelated = Path.Combine(Path.GetTempPath(), "other");

        Assert.False(SymlinkGuard.IsAncestorOrSelf(ancestor, unrelated));
    }

    /// <summary>
    /// Verifies that trailing directory separators on the candidate path are ignored
    /// before comparison.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_TrailingSeparator_IsIgnored()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var nested = Path.Combine(ancestor, "src") + Path.DirectorySeparatorChar;

        Assert.True(SymlinkGuard.IsAncestorOrSelf(ancestor, nested));
    }

    /// <summary>
    /// Verifies that resolving a plain (non-symlink) directory reports it as unresolved,
    /// so it is excluded from traversal as an ordinary directory rather than a link.
    /// </summary>
    [Fact]
    public void Resolve_PlainDirectory_ReturnsUnresolved()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var resolution = SymlinkGuard.Resolve(directory.FullName, ancestors: []);

            Assert.False(resolution.Resolved);
            Assert.Null(resolution.Target);
            Assert.False(resolution.IsLoop);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that resolving a nonexistent path does not throw and reports it as
    /// unresolved.
    /// </summary>
    [Fact]
    public void Resolve_NonexistentPath_ReturnsUnresolved()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var resolution = SymlinkGuard.Resolve(missing, ancestors: []);

        Assert.False(resolution.Resolved);
        Assert.Null(resolution.Target);
        Assert.False(resolution.IsLoop);
    }

    /// <summary>
    /// Verifies that a directory symlink resolves to its target and is flagged as a loop
    /// when the target is (or is nested under) one of the supplied ancestors.
    /// </summary>
    [Fact]
    public void Resolve_SymlinkLoopingToAncestor_ReturnsLoop()
    {
        var root = Directory.CreateTempSubdirectory();
        var linkPath = Path.Combine(root.FullName, "loop");
        try
        {
            if (!TryCreateDirectorySymlink(linkPath, root.FullName))
            {
                return;
            }

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [root.FullName]);

            Assert.True(resolution.Resolved);
            Assert.NotNull(resolution.Target);
            Assert.True(resolution.IsLoop);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }

            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a symlink resolving to a directory nested *under* (but not equal to)
    /// one of the ancestors is still flagged as a loop. This guards against the loop check
    /// being called with its two arguments swapped, which would only catch the
    /// target-equals-ancestor case and miss a target that is a proper descendant of an
    /// ancestor reached through an earlier followed symlink.
    /// </summary>
    [Fact]
    public void Resolve_SymlinkToDescendantOfAncestor_ReturnsLoop()
    {
        var root = Directory.CreateTempSubdirectory();
        var ancestor = Directory.CreateTempSubdirectory();
        var nestedInsideAncestor = ancestor.CreateSubdirectory("inner");
        var linkPath = Path.Combine(root.FullName, "link");
        try
        {
            if (!TryCreateDirectorySymlink(linkPath, nestedInsideAncestor.FullName))
            {
                return;
            }

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [ancestor.FullName]);

            Assert.True(resolution.Resolved);
            Assert.True(resolution.IsLoop);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }

            root.Delete(recursive: true);
            ancestor.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a directory symlink pointing outside any ancestor resolves without
    /// being flagged as a loop.
    /// </summary>
    [Fact]
    public void Resolve_SymlinkToUnrelatedDirectory_ReturnsNotLoop()
    {
        var root = Directory.CreateTempSubdirectory();
        var target = Directory.CreateTempSubdirectory();
        var linkPath = Path.Combine(root.FullName, "link");
        try
        {
            if (!TryCreateDirectorySymlink(linkPath, target.FullName))
            {
                return;
            }

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [root.FullName]);

            Assert.True(resolution.Resolved);
            Assert.False(resolution.IsLoop);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath, recursive: false);
            }

            root.Delete(recursive: true);
            target.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Creates a directory symlink at <paramref name="linkPath"/> pointing at
    /// <paramref name="targetPath"/>, returning <see langword="false"/> instead of
    /// throwing when the environment does not grant the privilege required (e.g.
    /// Windows without developer mode or elevation).
    /// </summary>
    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
