namespace Radirec.Core.Jellyfin;

/// <summary>取り込み完了後にメディアサーバのライブラリ更新を促す。</summary>
public interface ILibraryRefresher
{
    /// <summary>ライブラリの再スキャンを起動する(サーバ側で非同期実行、完了は待たない)。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>何もしない実装(Jellyfin 未設定時)。</summary>
public sealed class NullLibraryRefresher : ILibraryRefresher
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
