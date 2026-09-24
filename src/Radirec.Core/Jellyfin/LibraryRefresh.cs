using Radirec.Core.Ingestion;

namespace Radirec.Core.Jellyfin;

/// <summary>
/// 出力先を変えた後のライブラリ更新を起動し、状態を返す(取り込みと再適用で共有)。
/// 更新の失敗で、済んだコピー・移動は巻き戻さない。
/// </summary>
public static class LibraryRefresh
{
    public static async Task<(LibraryRefreshStatus Status, string? Error)> RunAsync(
        ILibraryRefresher refresher, CancellationToken cancellationToken)
    {
        // 未設定は NullLibraryRefresher で表される(refresh コマンドと同じ判定)。呼べば成功で終わるが、
        // 「起動」と報告すると更新されたと誤解させるので区別する(#133)。
        if (refresher is NullLibraryRefresher)
            return (LibraryRefreshStatus.NotConfigured, null);

        try
        {
            await refresher.RefreshAsync(cancellationToken);
            return (LibraryRefreshStatus.Started, null);
        }
        // 真のキャンセル(呼び出し元のトークン発火)は伝播させる。
        // HttpClient のタイムアウト(トークン未発火の TaskCanceledException)含むそれ以外の
        // 失敗は握って報告する。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (LibraryRefreshStatus.Failed, ex.Message);
        }
    }
}
