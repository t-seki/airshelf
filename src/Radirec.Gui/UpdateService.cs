using System.Runtime.Versioning;
using Radirec.Gui.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Radirec.Gui;

/// <summary>
/// アプリ内自己更新(Velopack。S6 / ADR-0013)。GitHub Releases をフィードに新版を照会・DL・適用する。
/// private repo のため config のトークンを <see cref="GithubSource"/> に渡す。**Velopack 未インストール
/// (dev/portable 実行)では何もしない**(<see cref="IsInstalled"/> が false)。
/// 管理画面のバージョン情報ページと自動の確認には <see cref="IAppUpdater"/> として渡す(#157)。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateService : IAppUpdater
{
    // 更新フィード = 本アプリ自身の GitHub リポジトリ(Release アセットを Velopack フィードにする)。
    private const string RepoUrl = "https://github.com/t-seki/radirec-tool";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public UpdateService(string? token, bool prerelease)
    {
        var source = new GithubSource(RepoUrl, string.IsNullOrWhiteSpace(token) ? null : token, prerelease);
        _manager = new UpdateManager(source);
    }

    /// <summary>Velopack でインストールされて動いているか(dev/portable 実行では false)。</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>保留中の更新版の文字列(無ければ null)。メニュー表示用。</summary>
    public string? PendingVersion => _pending?.TargetFullRelease?.Version?.ToString();

    /// <summary>
    /// 更新を照会し、**適用可能な**更新があれば保留にして true を返す。未インストール(dev)・更新無し・
    /// 版が取れない場合は false(通知したのに適用できない乖離を避けるため <see cref="PendingVersion"/> と
    /// 判定を一致させる)。例外(ネットワーク・認証失敗)は呼び出し側が握って UI を壊さない。
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        if (!_manager.IsInstalled)
            return false;

        var info = await _manager.CheckForUpdatesAsync();
        // 版が取れない UpdateInfo は「適用できない更新」として扱い保留にしない(PendingVersion と整合)。
        _pending = info?.TargetFullRelease?.Version is not null ? info : null;
        return _pending is not null;
    }

    /// <summary>
    /// 保留中の更新を DL し、<paramref name="drainBeforeRestart"/>(ワーカーの graceful 停止)を実行してから
    /// 適用・再起動する(成功時は戻らない)。DL はワーカー稼働中でよい(import-once)。差し替え直前にだけ
    /// 在中スキャンを畳んで SQLite を閉じ、取込中断・ファイルロックを避ける(S6 レビュー)。
    /// </summary>
    public async Task ApplyAndRestartAsync(Func<Task>? drainBeforeRestart)
    {
        var update = _pending;
        if (update is null)
            return;

        await _manager.DownloadUpdatesAsync(update);
        if (drainBeforeRestart is not null)
            await drainBeforeRestart();
        _manager.ApplyUpdatesAndRestart(update);
    }

    /// <summary>
    /// 適用の直前に取り込みを安全に止めるコールバック(<see cref="ApplyAndRestartAsync(Func{Task}?)"/> の引数)。
    /// ワーカーを起動した後に Program が入れる(ワーカーが無ければ null のまま)。<see cref="IAppUpdater"/> から適用するときに使う。
    /// </summary>
    public Func<Task>? DrainBeforeRestart { get; set; }

    Task IAppUpdater.ApplyAndRestartAsync() => ApplyAndRestartAsync(DrainBeforeRestart);
}
