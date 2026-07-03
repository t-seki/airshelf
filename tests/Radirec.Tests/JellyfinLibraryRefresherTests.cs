using System.Net;
using Radirec.Core.Jellyfin;

namespace Radirec.Tests;

public sealed class JellyfinLibraryRefresherTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Auth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Auth = request.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static HttpClient Client(StubHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://jelly.example.net/") };

    [Fact]
    public async Task RefreshAsync_PostsToLibraryRefresh_WithApiKeyHeader()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent);
        var sut = new JellyfinLibraryRefresher(Client(handler), "KEY123");

        await sut.RefreshAsync();

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://jelly.example.net/Library/Refresh", handler.Uri!.ToString());
        Assert.Equal("MediaBrowser Token=\"KEY123\"", handler.Auth);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RefreshAsync_OnErrorStatus_ThrowsJellyfinException(HttpStatusCode status)
    {
        var sut = new JellyfinLibraryRefresher(Client(new StubHandler(status)), "KEY");

        await Assert.ThrowsAsync<JellyfinException>(() => sut.RefreshAsync());
    }
}
