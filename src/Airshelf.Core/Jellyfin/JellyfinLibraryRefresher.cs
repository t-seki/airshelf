namespace Airshelf.Core.Jellyfin;

/// <summary>
/// Jellyfin の「全ライブラリスキャン」(<c>POST /Library/Refresh</c>)を起動する。
/// <paramref name="http"/> の BaseAddress に Jellyfin のルート URL(末尾スラッシュ付き)を設定しておくこと。
/// </summary>
public sealed class JellyfinLibraryRefresher(HttpClient http, string apiKey) : ILibraryRefresher
{
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Library/Refresh");
        // Jellyfin の API キー認証(MediaBrowser スキーム)。
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new JellyfinException(
                $"Jellyfin ライブラリ更新に失敗しました (HTTP {(int)response.StatusCode} {response.StatusCode})。" +
                "URL / API キーを確認してください。");
    }
}
