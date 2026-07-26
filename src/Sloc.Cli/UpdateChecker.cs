using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Sloc.Cli;

/// <summary>
/// The result of a successful update check: a newer release is available.
/// </summary>
/// <param name="LatestVersion">The latest released version (without the leading "v").</param>
/// <param name="ReleaseUrl">The URL of the release page to download it from.</param>
public sealed record UpdateCheckResult(string LatestVersion, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer version of sloc than the one currently running.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/coldhighsun/Sloc/releases/latest";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateChecker"/> using a process-wide
    /// shared <see cref="HttpClient"/>.
    /// </summary>
    public UpdateChecker()
        : this(SharedHttpClient)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="UpdateChecker"/> with a specific
    /// <see cref="HttpClient"/>. Intended for testing with a stubbed message handler.
    /// </summary>
    /// <param name="httpClient">The client used to query the GitHub API.</param>
    internal UpdateChecker(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var release = await _httpClient
                .GetFromJsonAsync<GitHubRelease>(LatestReleaseUrl, linkedCts.Token)
                .ConfigureAwait(false);

            if (release?.TagName is null || release.HtmlUrl is null)
            {
                return null;
            }

            var latestVersionText = release.TagName.StartsWith('v') ? release.TagName[1..] : release.TagName;

            if (!Version.TryParse(StripPrereleaseSuffix(latestVersionText), out var latest) ||
                !Version.TryParse(StripPrereleaseSuffix(currentVersion), out var current))
            {
                return null;
            }

            return latest > current ? new UpdateCheckResult(latestVersionText, release.HtmlUrl) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strips a semver prerelease/build-metadata suffix (e.g. "-beta.1" or "+abc123") so the
    /// remaining "major.minor.patch" prefix can be parsed by <see cref="Version"/>.
    /// </summary>
    private static string StripPrereleaseSuffix(string version)
    {
        var suffixIndex = version.IndexOfAny(['-', '+']);
        return suffixIndex < 0 ? version : version[..suffixIndex];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("sloc-cli");
        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}
