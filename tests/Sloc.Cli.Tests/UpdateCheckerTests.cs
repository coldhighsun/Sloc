using Sloc.Cli;
using System.Net;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="UpdateChecker"/>, using a stubbed HTTP handler so
/// no real network call is made.
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>
    /// Verifies that a newer release than the current version is reported.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_NewerRelease_ReturnsResult()
    {
        var checker = new UpdateChecker(StubClient(HttpStatusCode.OK,
            """{"tag_name":"v2.0.0","html_url":"https://example.com/releases/2.0.0"}"""));

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result!.LatestVersion);
        Assert.Equal("https://example.com/releases/2.0.0", result.ReleaseUrl);
    }

    /// <summary>
    /// Verifies that no update is reported when the latest release is not newer.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ReturnsNull()
    {
        var checker = new UpdateChecker(StubClient(HttpStatusCode.OK,
            """{"tag_name":"v1.0.0","html_url":"https://example.com/releases/1.0.0"}"""));

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that an HTTP failure is swallowed and reported as no update.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_HttpFailure_ReturnsNull()
    {
        var checker = new UpdateChecker(new HttpClient(new ThrowingHandler()));

        var result = await checker.CheckForUpdateAsync("1.0.0", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(result);
    }

    private static HttpClient StubClient(HttpStatusCode status, string body) =>
        new(new StubHandler(status, body));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network unreachable");
    }
}
