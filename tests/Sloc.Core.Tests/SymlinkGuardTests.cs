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
    /// Verifies that comparison matches the platform's own file system: case-insensitive on
    /// Windows and macOS, case-sensitive elsewhere.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_DifferentCasing_MatchesPlatformCaseSensitivity()
    {
        var ancestor = Path.Combine(Path.GetTempPath(), "repo");
        var nested = Path.Combine(ancestor, "src").ToUpperInvariant();

        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, SymlinkGuard.IsAncestorOrSelf(ancestor, nested));
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

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [SymlinkGuard.GetRealPath(root.FullName)!]);

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

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [SymlinkGuard.GetRealPath(ancestor.FullName)!]);

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

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [SymlinkGuard.GetRealPath(root.FullName)!]);

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
    /// Verifies that a symlink whose target contains one of the ancestors (e.g. a link to the
    /// scan root's parent) is flagged as a loop, since following it would walk the ancestor
    /// again.
    /// </summary>
    [Fact]
    public void Resolve_SymlinkToParentOfAncestor_ReturnsLoop()
    {
        var parent = Directory.CreateTempSubdirectory();
        try
        {
            var root = parent.CreateSubdirectory("root");
            var linkPath = Path.Combine(root.FullName, "up");
            if (!TryCreateDirectorySymlink(linkPath, parent.FullName))
            {
                return;
            }

            var resolution = SymlinkGuard.Resolve(linkPath, ancestors: [SymlinkGuard.GetRealPath(root.FullName)!]);

            Assert.True(resolution.Resolved);
            Assert.True(resolution.IsLoop);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="SymlinkGuard.GetRealPath"/> resolves a link in the middle of a
    /// path, and a relative link chained to another link, to the real location.
    /// </summary>
    [Fact]
    public void GetRealPath_PathThroughLinks_ResolvesEveryComponent()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var target = directory.CreateSubdirectory("target");
            File.WriteAllText(Path.Combine(target.FullName, "file.txt"), "x");
            var absoluteLink = Path.Combine(directory.FullName, "absolute");
            var relativeLink = Path.Combine(directory.FullName, "relative");
            if (!TryCreateDirectorySymlink(absoluteLink, target.FullName)
                || !TryCreateDirectorySymlink(relativeLink, "absolute"))
            {
                return;
            }

            var expected = Path.Combine(SymlinkGuard.GetRealPath(target.FullName)!, "file.txt");

            Assert.Equal(expected, SymlinkGuard.GetRealPath(Path.Combine(absoluteLink, "file.txt")));
            Assert.Equal(expected, SymlinkGuard.GetRealPath(Path.Combine(relativeLink, "file.txt")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="SymlinkGuard.GetRealPath"/> returns <see langword="null"/> for
    /// a missing path, a dangling link, and a cycle of links.
    /// </summary>
    [Fact]
    public void GetRealPath_MissingOrLoopingPath_ReturnsNull()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Null(SymlinkGuard.GetRealPath(Path.Combine(directory.FullName, "missing", "file.txt")));

            var first = Path.Combine(directory.FullName, "first");
            var second = Path.Combine(directory.FullName, "second");
            var dangling = Path.Combine(directory.FullName, "dangling");
            if (!TryCreateDirectorySymlink(first, second)
                || !TryCreateDirectorySymlink(second, first)
                || !TryCreateDirectorySymlink(dangling, Path.Combine(directory.FullName, "gone")))
            {
                return;
            }

            Assert.Null(SymlinkGuard.GetRealPath(first));
            Assert.Null(SymlinkGuard.GetRealPath(dangling));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="SymlinkGuard.GetRealPath"/> returns a path without links
    /// unchanged apart from normalization.
    /// </summary>
    [Fact]
    public void GetRealPath_PlainPath_ReturnsFullPath()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var real = SymlinkGuard.GetRealPath(directory.FullName)!;
            var nested = Directory.CreateDirectory(Path.Combine(real, "a", "b"));

            Assert.Equal(nested.FullName.TrimEnd(Path.DirectorySeparatorChar), SymlinkGuard.GetRealPath(nested.FullName + Path.DirectorySeparatorChar));
        }
        finally
        {
            directory.Delete(recursive: true);
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

    /// <summary>
    /// Verifies that a filesystem root (which keeps its trailing separator, e.g. <c>C:\</c>
    /// or <c>/</c>) is recognized as the ancestor of paths below it.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_FilesystemRoot_ReturnsTrueForNestedPath()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var nested = Path.Combine(root, "repo", "src");

        Assert.True(SymlinkGuard.IsAncestorOrSelf(root, nested));
        Assert.True(SymlinkGuard.IsAncestorOrSelf(root, root));
    }

    /// <summary>
    /// Verifies that a reparse point with no link target (e.g. a OneDrive Files-On-Demand
    /// folder or cloud file, whose attributes include <see cref="FileAttributes.ReparsePoint"/>)
    /// is not a link, so the scan treats it as an ordinary file or directory instead of
    /// silently skipping it.
    /// </summary>
    [Fact]
    public void IsLink_ReparsePointWithoutLinkTarget_ReturnsFalse()
    {
        // The attributes of a OneDrive folder: Directory | ReparsePoint | Unpinned.
        const FileAttributes oneDriveFolder = FileAttributes.Directory | FileAttributes.ReparsePoint | (FileAttributes)0x100000;

        Assert.False(SymlinkGuard.IsLink(oneDriveFolder, () => null));
    }

    /// <summary>
    /// Verifies that a reparse point with a link target (a symlink or junction) is a link.
    /// </summary>
    [Fact]
    public void IsLink_ReparsePointWithLinkTarget_ReturnsTrue()
    {
        Assert.True(SymlinkGuard.IsLink(FileAttributes.Directory | FileAttributes.ReparsePoint, () => "C:\\target"));
    }

    /// <summary>
    /// Verifies that an entry without the reparse-point attribute is not a link, without
    /// paying for a link-target lookup.
    /// </summary>
    [Fact]
    public void IsLink_NotAReparsePoint_ReturnsFalseWithoutReadingLinkTarget()
    {
        Assert.False(SymlinkGuard.IsLink(FileAttributes.Directory, () => throw new InvalidOperationException("should not be read")));
    }

    /// <summary>
    /// Verifies that online-only placeholders (offline or recall-on-open/data-access) are
    /// recognized as cloud-only, while a hydrated OneDrive file is not.
    /// </summary>
    /// <param name="attributes">The file attributes, as their raw value.</param>
    /// <param name="expected">Whether the file is cloud-only.</param>
    [Theory]
    [InlineData(0x00400420, true)] // Archive | ReparsePoint | RecallOnDataAccess (online-only)
    [InlineData(0x00041020, true)] // Archive | Offline | RecallOnOpen
    [InlineData(0x00080420, false)] // Archive | ReparsePoint | Pinned (available locally)
    [InlineData(0x00000020, false)] // Archive
    public void IsCloudOnly_ReflectsRecallAttributes(int attributes, bool expected)
    {
        Assert.Equal(expected, SymlinkGuard.IsCloudOnly((FileAttributes)attributes));
    }

    /// <summary>
    /// Verifies that <see cref="SymlinkGuard.CanEnumerate"/> is true for a listable directory
    /// and false (without throwing) for one that can't be listed.
    /// </summary>
    [Fact]
    public void CanEnumerate_DistinguishesListableDirectory()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            Assert.True(SymlinkGuard.CanEnumerate(directory.FullName));
            Assert.False(SymlinkGuard.CanEnumerate(Path.Combine(directory.FullName, "missing")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies <see cref="SymlinkGuard.IsLink(FileSystemInfo)"/> against the real filesystem:
    /// a plain directory is not a link, a directory symlink is.
    /// </summary>
    [Fact]
    public void IsLink_RealDirectoryAndSymlink_AreDistinguished()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var target = directory.CreateSubdirectory("target");
            Assert.False(SymlinkGuard.IsLink(target));

            var linkPath = Path.Combine(directory.FullName, "link");
            try
            {
                Directory.CreateSymbolicLink(linkPath, target.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory symlinks require a privilege this environment may not grant.
                return;
            }

            Assert.True(SymlinkGuard.IsLink(new DirectoryInfo(linkPath)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
