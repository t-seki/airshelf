namespace Airshelf.Cli.Core.Output;

/// <summary><c>refresh</c> の結果。</summary>
public enum RefreshStatus
{
    /// <summary>ライブラリ更新を起動した(サーバ側で非同期に走る。完了は待たない)。</summary>
    Started,

    /// <summary>Jellyfin が未設定(airshelf.json の JellyfinUrl / JellyfinApiKey が空)。</summary>
    NotConfigured,

    /// <summary>設定が不正(JellyfinUrl の形式が不正)で呼べない。</summary>
    Misconfigured,

    /// <summary>Jellyfin に繋がらない・エラーを返した・時間切れ。</summary>
    Failed,
}

/// <summary><c>refresh --json</c> の <c>result</c>。</summary>
/// <param name="Error">起動しなかった理由(<c>started</c> のときは null)。</param>
public sealed record RefreshJson(RefreshStatus Status, string? Error);
