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

            if (!SemVersion.TryParse(latestVersionText, out var latest) ||
                !SemVersion.TryParse(currentVersion, out var current))
            {
                return null;
            }

            return SemVersion.ComparePrecedence(latest, current) > 0
                ? new UpdateCheckResult(latestVersionText, release.HtmlUrl)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A minimal SemVer version: the "major.minor.patch" core plus an optional prerelease label.
    /// Build metadata ("+...") is discarded because it does not affect precedence (SemVer §10).
    /// </summary>
    /// <param name="Core">The parsed "major.minor.patch" core.</param>
    /// <param name="Prerelease">The prerelease label (e.g. "alpha.1"), or <see langword="null"/> for a stable release.</param>
    private sealed record SemVersion(Version Core, string? Prerelease)
    {
        /// <summary>
        /// Parses a version string into its core and prerelease parts.
        /// </summary>
        /// <param name="text">The version text (without a leading "v").</param>
        /// <param name="version">The parsed version, when successful.</param>
        /// <returns><see langword="true"/> if the core "major.minor.patch" parsed; otherwise <see langword="false"/>.</returns>
        public static bool TryParse(string text, out SemVersion version)
        {
            version = null!;

            // Drop build metadata first; it never affects precedence.
            var buildIndex = text.IndexOf('+');
            if (buildIndex >= 0)
            {
                text = text[..buildIndex];
            }

            var dashIndex = text.IndexOf('-');
            var coreText = dashIndex < 0 ? text : text[..dashIndex];
            var prerelease = dashIndex < 0 ? null : text[(dashIndex + 1)..];

            if (!Version.TryParse(coreText, out var core))
            {
                return false;
            }

            version = new SemVersion(core, string.IsNullOrEmpty(prerelease) ? null : prerelease);
            return true;
        }

        /// <summary>
        /// Compares two versions by SemVer precedence (SemVer §11).
        /// </summary>
        /// <param name="left">The left-hand version.</param>
        /// <param name="right">The right-hand version.</param>
        /// <returns>A negative value if <paramref name="left"/> is lower, zero if equal, positive if higher.</returns>
        public static int ComparePrecedence(SemVersion left, SemVersion right)
        {
            var coreComparison = left.Core.CompareTo(right.Core);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            // Equal cores: a version with a prerelease label has lower precedence than one without.
            if (left.Prerelease is null && right.Prerelease is null)
            {
                return 0;
            }

            if (left.Prerelease is null)
            {
                return 1;
            }

            if (right.Prerelease is null)
            {
                return -1;
            }

            return ComparePrerelease(left.Prerelease, right.Prerelease);
        }

        /// <summary>
        /// Compares two prerelease labels by dot-separated identifiers (SemVer §11).
        /// </summary>
        /// <param name="left">The left-hand prerelease label.</param>
        /// <param name="right">The right-hand prerelease label.</param>
        /// <returns>A negative value if <paramref name="left"/> is lower, zero if equal, positive if higher.</returns>
        private static int ComparePrerelease(string left, string right)
        {
            var leftParts = left.Split('.');
            var rightParts = right.Split('.');
            var shared = Math.Min(leftParts.Length, rightParts.Length);

            for (var i = 0; i < shared; i++)
            {
                var leftIsNumeric = int.TryParse(leftParts[i], out var leftNumber);
                var rightIsNumeric = int.TryParse(rightParts[i], out var rightNumber);

                if (leftIsNumeric && rightIsNumeric)
                {
                    var numberComparison = leftNumber.CompareTo(rightNumber);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }
                }
                else if (leftIsNumeric != rightIsNumeric)
                {
                    // Numeric identifiers always have lower precedence than alphanumeric ones.
                    return leftIsNumeric ? -1 : 1;
                }
                else
                {
                    var textComparison = string.CompareOrdinal(leftParts[i], rightParts[i]);
                    if (textComparison != 0)
                    {
                        return textComparison;
                    }
                }
            }

            // All shared identifiers equal: the label with more identifiers has higher precedence.
            return leftParts.Length.CompareTo(rightParts.Length);
        }
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
