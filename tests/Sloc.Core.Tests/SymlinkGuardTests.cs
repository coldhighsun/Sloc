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
        Assert.True(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\repo"));
    }

    /// <summary>
    /// Verifies that a path nested under the ancestor is recognized as such.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_NestedPath_ReturnsTrue()
    {
        Assert.True(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\repo\src\sub"));
    }

    /// <summary>
    /// Verifies that comparison is case-insensitive, matching Windows path semantics.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_DifferentCasing_ReturnsTrue()
    {
        Assert.True(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\REPO\Src"));
    }

    /// <summary>
    /// Verifies that a sibling directory whose name merely shares the ancestor's name as
    /// a prefix is not treated as nested under it.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_PrefixWithoutSeparator_ReturnsFalse()
    {
        Assert.False(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\repo-extra"));
    }

    /// <summary>
    /// Verifies that an unrelated path is not treated as nested under the ancestor.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_UnrelatedPath_ReturnsFalse()
    {
        Assert.False(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\other"));
    }

    /// <summary>
    /// Verifies that trailing directory separators on the candidate path are ignored
    /// before comparison.
    /// </summary>
    [Fact]
    public void IsAncestorOrSelf_TrailingSeparator_IsIgnored()
    {
        Assert.True(SymlinkGuard.IsAncestorOrSelf(@"C:\repo", @"C:\repo\src\"));
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
    /// Verifies that a directory junction resolves to its target and is flagged as a loop
    /// when the target is (or is nested under) one of the supplied ancestors.
    /// </summary>
    [Fact]
    public void Resolve_JunctionLoopingToAncestor_ReturnsLoop()
    {
        var root = Directory.CreateTempSubdirectory();
        var junctionPath = Path.Combine(root.FullName, "loop");
        try
        {
            RequireJunctionCreated(junctionPath, root.FullName);

            var resolution = SymlinkGuard.Resolve(junctionPath, ancestors: [root.FullName]);

            Assert.True(resolution.Resolved);
            Assert.NotNull(resolution.Target);
            Assert.True(resolution.IsLoop);
        }
        finally
        {
            if (Directory.Exists(junctionPath)) { Directory.Delete(junctionPath, recursive: false); }
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a directory junction pointing outside any ancestor resolves without
    /// being flagged as a loop.
    /// </summary>
    [Fact]
    public void Resolve_JunctionToUnrelatedDirectory_ReturnsNotLoop()
    {
        var root = Directory.CreateTempSubdirectory();
        var target = Directory.CreateTempSubdirectory();
        var junctionPath = Path.Combine(root.FullName, "link");
        try
        {
            RequireJunctionCreated(junctionPath, target.FullName);

            var resolution = SymlinkGuard.Resolve(junctionPath, ancestors: [root.FullName]);

            Assert.True(resolution.Resolved);
            Assert.False(resolution.IsLoop);
        }
        finally
        {
            if (Directory.Exists(junctionPath)) { Directory.Delete(junctionPath, recursive: false); }
            root.Delete(recursive: true);
            target.Delete(recursive: true);
        }
    }

    private static void RequireJunctionCreated(string junctionPath, string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start mklink process.");
        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException("Unable to create a directory junction in this environment.");
        }
    }
}
