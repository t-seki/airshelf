using Radirec.Core.Jellyfin;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// <see cref="CompositionRoot.BuildRefresher"/> の分岐を検証する。未設定は何もしない refresher、
/// JellyfinUrl の形式不正は未設定と区別して「呼ぶと失敗する」refresher にする(#133)。
/// </summary>
public class CompositionRootRefresherTests
{
    [Theory]
    [InlineData("", "key")]
    [InlineData("http://jellyfin:8096", "")]
    [InlineData(" ", " ")]
    public void Missing_url_or_key_yields_null_refresher(string url, string apiKey)
    {
        var (refresher, disposable) = CompositionRoot.BuildRefresher(
            new RadirecConfig { JellyfinUrl = url, JellyfinApiKey = apiKey });

        Assert.IsType<NullLibraryRefresher>(refresher);
        Assert.Null(disposable);
    }

    [Fact]
    public async Task Malformed_url_yields_refresher_that_fails_with_reason()
    {
        var (refresher, disposable) = CompositionRoot.BuildRefresher(
            new RadirecConfig { JellyfinUrl = "not a url", JellyfinApiKey = "key" });

        Assert.IsNotType<NullLibraryRefresher>(refresher);
        Assert.Null(disposable);
        var ex = await Assert.ThrowsAsync<JellyfinException>(() => refresher.RefreshAsync());
        Assert.Contains("JellyfinUrl の形式が不正", ex.Message);
    }

    [Fact]
    public void Valid_url_and_key_yields_jellyfin_refresher()
    {
        var (refresher, disposable) = CompositionRoot.BuildRefresher(
            new RadirecConfig { JellyfinUrl = "http://jellyfin:8096", JellyfinApiKey = "key" });
        using (disposable)
        {
            Assert.IsType<JellyfinLibraryRefresher>(refresher);
            Assert.NotNull(disposable);
        }
    }
}
