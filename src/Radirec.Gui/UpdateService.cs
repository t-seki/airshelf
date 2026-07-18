using System.Runtime.Versioning;
using Velopack;
using Velopack.Sources;

namespace Radirec.Gui;

/// <summary>
/// アプリ内自己更新(Velopack。S6 / ADR-0013)。GitHub Releases をフィードに新版を照会・DL・適用する。
/// private repo のため config のトークンを <see cref="GithubSource"/> に渡す。**Velopack 未インストール
/// (dev/portable 実行)では何もしない**(<see cref="IsInstalled"/> が false)。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateService
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
    /// 更新を照会し、あれば保留にして true を返す。未インストール(dev)や更新無しは false。
    /// 例外(ネットワーク・認証失敗)は呼び出し側が握って UI を壊さない。
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        if (!_manager.IsInstalled)
            return false;

        _pending = await _manager.CheckForUpdatesAsync();
        return _pending is not null;
    }

    /// <summary>保留中の更新を DL・適用して再起動する(成功時は戻らない)。</summary>
    public async Task ApplyAndRestartAsync()
    {
        var update = _pending;
        if (update is null)
            return;

        await _manager.DownloadUpdatesAsync(update);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
