using GitHubReleaseUpdater;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.Versioning;

namespace Sloc.Cli.Updates;

/// <summary>
/// The result of a successful update check: a newer release is available.
/// </summary>
/// <param name="LatestVersion">The latest released version (without the leading "v").</param>
/// <param name="ReleaseUrl">The URL of the release page to download it from.</param>
public sealed record UpdateCheckResult(string LatestVersion, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer version of sloc than the one currently running, using
/// the <c>GitHubReleaseUpdater</c> package for the release lookup and SemVer 2.0 precedence
/// comparison instead of a hand-rolled HTTP client and version parser.
/// </summary>
public sealed class UpdateChecker
{
    private const string Owner = "coldhighsun";
    private const string Repo = "Sloc";

    private readonly IGitHubReleaseClient? _client;

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateChecker"/> that talks to the real
    /// GitHub API through a process-wide shared <see cref="HttpClient"/>.
    /// </summary>
    public UpdateChecker()
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateChecker"/> over a specific
    /// <see cref="IGitHubReleaseClient"/>. Intended for testing with a stub client, so no
    /// real network call is made.
    /// </summary>
    /// <param name="client">The client used to query GitHub Releases.</param>
    internal UpdateChecker(IGitHubReleaseClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Checks whether a newer stable release than <paramref name="currentVersion"/> is available.
    /// </summary>
    /// <param name="currentVersion">The version currently running.</param>
    /// <param name="timeout">The maximum time to wait for the GitHub API to respond.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    /// The newer release, or <see langword="null"/> if there is no update or the check failed for any reason
    /// (network unreachable, timeout, rate limit, etc). Failures are swallowed intentionally: an update check
    /// must never break or delay a normal analysis run.
    /// </returns>
    public async Task<UpdateCheckResult?> CheckForUpdateAsync(
        string currentVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var options = new UpdaterOptions
            {
                Owner = Owner,
                Repo = Repo,
                CurrentVersion = current
            };

            using var updater = _client is null ? new ReleaseUpdater(options) : new ReleaseUpdater(options, _client);
            var check = await updater.CheckForUpdateAsync(cancellationToken: linkedCts.Token).ConfigureAwait(false);

            if (check.Update is not { } update || update.Release.HtmlUrl is not { } releaseUrl)
            {
                return null;
            }

            return new UpdateCheckResult(update.Version.ToString(), releaseUrl);
        }
        catch
        {
            return null;
        }
    }
}
