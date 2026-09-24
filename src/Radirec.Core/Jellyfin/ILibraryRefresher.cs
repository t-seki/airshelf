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

/// <summary>
/// 設定が不正で更新できない(JellyfinUrl の形式不正等)ことを、呼ばれるたびに <see cref="JellyfinException"/> で知らせる。
/// 未設定(<see cref="NullLibraryRefresher"/>)と区別し、設定の誤りを失敗として表に出すため(#133)。
/// </summary>
public sealed class MisconfiguredLibraryRefresher(string message) : ILibraryRefresher
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new JellyfinException(message));
}
