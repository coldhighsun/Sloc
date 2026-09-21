using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using Sloc.Cli.Updates;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="UpdateChecker"/>, using a stub
/// <see cref="IGitHubReleaseClient"/> so no real network call is made.
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>
    /// Verifies that a client failure is swallowed and reported as no update.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_ClientFailure_ReturnsNull()
    {
        var checker = new UpdateChecker(new ThrowingClient());

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a newer release than the current version is reported.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_NewerRelease_ReturnsResult()
    {
        var checker = new UpdateChecker(StubClient("v2.0.0", "https://example.com/releases/2.0.0"));

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.Equal("https://example.com/releases/2.0.0", result.ReleaseUrl);
    }

    /// <summary>
    /// Verifies that no update is reported when the latest release is not newer.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ReturnsNull()
    {
        var checker = new UpdateChecker(StubClient("v1.0.0", "https://example.com/releases/1.0.0"));

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a two-part release tag (e.g. <c>v1.2</c>) is not treated as lower than
    /// the equivalent three-part running version <c>1.2.0</c>: both parse to the same
    /// major/minor/patch core, so neither is reported as an update over the other. Regression
    /// test for the old hand-rolled parser's use of <see cref="Version.TryParse"/>, which
    /// padded a missing component with -1 instead of 0 and made "1.2" compare as lower than
    /// "1.2.0" even though they represent the same release.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_TwoPartTagEquivalentToThreePartCurrent_ReturnsNull()
    {
        var checker = new UpdateChecker(StubClient("v1.2", "https://example.com/releases/1.2"));

        var result = await checker.CheckForUpdateAsync("1.2.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a stable release is reported as an update over a same-numbered prerelease
    /// (e.g. running 2.2.0-alpha.1 when 2.2.0 is out).
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_PrereleaseLocalStableRemote_ReturnsResult()
    {
        var checker = new UpdateChecker(StubClient("v2.2.0", "https://example.com/releases/2.2.0"));

        var result = await checker.CheckForUpdateAsync("2.2.0-alpha.1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("2.2.0", result.LatestVersion);
    }

    /// <summary>
    /// Verifies that a same-numbered prerelease is not reported as an update over the stable release.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_StableLocalPrereleaseRemote_ReturnsNull()
    {
        var checker = new UpdateChecker(StubClient("v2.2.0-alpha.1", "https://example.com/releases/2.2.0-alpha.1"));

        var result = await checker.CheckForUpdateAsync("2.2.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a higher-numbered prerelease is reported as an update over a lower one.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_TwoPrereleases_ReturnsResult()
    {
        var checker = new UpdateChecker(StubClient("v2.2.0-alpha.2", "https://example.com/releases/2.2.0-alpha.2"));

        var result = await checker.CheckForUpdateAsync("2.2.0-alpha.1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("2.2.0-alpha.2", result.LatestVersion);
    }

    /// <summary>
    /// Verifies that no update is reported when local and remote are the same prerelease.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_SamePrerelease_ReturnsNull()
    {
        var checker = new UpdateChecker(StubClient("v2.2.0-alpha.1", "https://example.com/releases/2.2.0-alpha.1"));

        var result = await checker.CheckForUpdateAsync("2.2.0-alpha.1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that an unparseable running version is treated as no update, rather than
    /// throwing or hitting the network.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_UnparseableCurrentVersion_ReturnsNull()
    {
        var checker = new UpdateChecker(new ThrowingClient());

        var result = await checker.CheckForUpdateAsync("not-a-version", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    private static StubGitHubClient StubClient(string tagName, string htmlUrl) =>
        new(new GitHubRelease { TagName = tagName, HtmlUrl = htmlUrl });

    private sealed class StubGitHubClient(GitHubRelease? release) : IGitHubReleaseClient
    {
        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) =>
            Task.FromResult(release);

        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingClient : IGitHubReleaseClient
    {
        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("network unreachable");

        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
