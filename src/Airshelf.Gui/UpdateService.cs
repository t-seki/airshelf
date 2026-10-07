using System.Runtime.Versioning;
using Airshelf.Gui.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Airshelf.Gui;

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
    private readonly ApplyGuard _applyGuard = new(); // トレイとバージョン情報ページの 2 つの入口の再入を、ここで防ぐ(#157)
    private UpdateInfo? _pending;

    public UpdateService(string? token, bool prerelease)
    {
        var source = new GithubSource(RepoUrl, string.IsNullOrWhiteSpace(token) ? null : token, prerelease);
        _manager = new UpdateManager(source);
    }

    /// <summary>Velopack でインストールされて動いているか(dev/portable 実行では false)。</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>
    /// 設定(トークン)なしで、Velopack でインストールされて動いているかを調べる(#157。設定を読めないときのバージョン情報ページ用)。
    /// <c>UpdateManager.IsInstalled</c> は Locator(<c>VelopackLocator.Current</c>)の <c>CurrentlyInstalledVersion</c> が
    /// あるかだけを見て、Source(トークンを持つ)には触れない(Velopack 1.2.0 の IL で確かめた)。なのでトークンの無い Source で
    /// 作った UpdateManager でも、同じ判定になる。コンストラクタは Source を覚えるだけで、照会はしない。
    /// 調べられなかったら null(呼び出し側は、分からないものとして扱う)。
    /// </summary>
    public static bool? ProbeInstalled()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false)).IsInstalled;
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"インストール済みかどうかを調べられませんでした: {ex.Message}");
            return null;
        }
    }

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
    /// 入口はトレイとバージョン情報ページの 2 つある。適用の途中(DL を待っている間など)にもう一度呼ばれたら、何もしない
    /// (<see cref="ApplyGuard"/>。DL・取り込みの停止・差し替えを二重に走らせない)。
    /// </summary>
    public Task ApplyAndRestartAsync(Func<Task>? drainBeforeRestart) =>
        _applyGuard.RunAsync(async () =>
        {
            var update = _pending;
            if (update is null)
                return;

            await _manager.DownloadUpdatesAsync(update);
            if (drainBeforeRestart is not null)
                await drainBeforeRestart();
            _manager.ApplyUpdatesAndRestart(update);
        });

    /// <summary>
    /// 適用の直前に取り込みを安全に止めるコールバック(<see cref="ApplyAndRestartAsync(Func{Task}?)"/> の引数)。
    /// ワーカーを起動した後に Program が入れる(ワーカーが無ければ null のまま)。<see cref="IAppUpdater"/> から適用するときに使う。
    /// </summary>
    public Func<Task>? DrainBeforeRestart { get; set; }

    Task IAppUpdater.ApplyAndRestartAsync() => ApplyAndRestartAsync(DrainBeforeRestart);
}
