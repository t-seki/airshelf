using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Airshelf.Gui.Core.Updates;

namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 管理画面のバージョン情報ページ(#157)。版 / 今すぐ更新を確認 / 更新を適用して再起動(保留中だけ)。
/// 結果はバルーンではなくページの中に出す(W-503〜W-506・W-509)。更新の項目を出せないときは、理由を出す
/// (<see cref="UpdateSupport"/>):開発版・portable(Velopack で入れていない版)は W-510、Velopack で入れた版で設定を
/// 読めないときは W-511。
/// <para>
/// 自動の確認(トレイ)と同じ <see cref="UpdateChecker"/> を使うので、重なったら W-505 を返す。ページを開いている間に
/// 自動の確認が更新を保留にしたら、シェルのタイマー(<see cref="Refresh"/>)が「更新を適用して再起動」を出す。
/// </para>
/// </summary>
public sealed class AboutPageViewModel : ObservableObject
{
    private readonly IAppUpdater? _updater;
    private readonly UpdateChecker? _checker;
    private readonly Func<string, bool> _confirmApply;
    private readonly Action<string> _logWarn;

    private string? _pendingVersion;
    private string? _message;
    private bool _messageIsWarning;

    /// <param name="version">表示する版(W-501)。</param>
    /// <param name="support">更新の項目を出せるか。出せないときの理由(W-510 / W-511)。</param>
    /// <param name="updater">更新の仕組み。作れなかった(設定を読めない等)なら null。</param>
    /// <param name="checker">更新の確認(トレイの自動の確認と共有する)。<paramref name="updater"/> が無ければ null。</param>
    /// <param name="confirmApply">適用の確認(W-508 の文言を受け取り、OK なら true)。シェルがダイアログを出す。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    public AboutPageViewModel(
        string version,
        UpdateSupport support,
        IAppUpdater? updater,
        UpdateChecker? checker,
        Func<string, bool> confirmApply,
        Action<string> logWarn)
    {
        VersionText = UpdateTexts.VersionLine(version);
        Support = support;
        // Supported でも、更新の仕組みか確認を作れなかった(初期化の失敗。gui.log にある)ときは、理由を出さずに項目を隠す。
        CanUpdate = support == UpdateSupport.Supported && updater is { IsInstalled: true } && checker is not null;
        _updater = CanUpdate ? updater : null;
        _checker = CanUpdate ? checker : null;
        _confirmApply = confirmApply;
        _logWarn = logWarn;

        CheckCommand = new AsyncRelayCommand(CheckAsync, () => CanUpdate);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => HasPending);

        Refresh();
    }

    /// <summary>W-501。</summary>
    public string VersionText { get; }

    /// <summary>更新の項目を出せるか。出せないときの理由。</summary>
    public UpdateSupport Support { get; }

    /// <summary>更新の項目(W-502・W-507)を出すか。</summary>
    public bool CanUpdate { get; }

    /// <summary>W-510 を出すか(Velopack で入れていない版)。</summary>
    public bool IsNotInstalled => Support == UpdateSupport.NotInstalled;

    /// <summary>W-511 を出すか(Velopack で入れた版で、設定を読めない)。</summary>
    public bool IsConfigUnreadable => Support == UpdateSupport.ConfigUnreadable;

    /// <summary>W-502。</summary>
    public AsyncRelayCommand CheckCommand { get; }

    /// <summary>W-507。保留中だけ押せる。押すと W-508 で確かめてから適用する。</summary>
    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>保留中の更新の版。無ければ null。</summary>
    public string? PendingVersion
    {
        get => _pendingVersion;
        private set
        {
            if (!SetProperty(ref _pendingVersion, value))
                return;
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(ApplyButtonText));
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>「更新を適用して再起動」を出すか(保留中だけ)。</summary>
    public bool HasPending => PendingVersion is not null;

    /// <summary>W-507。</summary>
    public string ApplyButtonText => PendingVersion is { } version ? UpdateTexts.ApplyButton(version) : "";

    /// <summary>確認・適用の結果(W-503〜W-506・W-509)。まだ何もしていなければ null。</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary><see cref="Message"/> が失敗(W-506・W-509 の 2 つ目)か。</summary>
    public bool MessageIsWarning { get => _messageIsWarning; private set => SetProperty(ref _messageIsWarning, value); }

    /// <summary>
    /// 保留中の更新を読み直す。管理画面が開いている間、シェルのタイマーが呼ぶ(トレイの自動の確認が見つけた更新や、
    /// 取り下げで消えた保留を反映する)。
    /// </summary>
    public void Refresh()
    {
        if (_updater is null)
            return;
        try
        {
            PendingVersion = _updater.PendingVersion;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 保留中の更新を読めませんでした: {ex}");
        }
    }

    private async Task CheckAsync()
    {
        if (_checker is null)
            return;

        var result = await _checker.CheckAsync(manual: true);
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                Show(UpdateTexts.UpToDate, warning: false);
                break;
            case UpdateCheckOutcome.Found:
                Show(UpdateTexts.Found(result.Version), warning: false);
                break;
            case UpdateCheckOutcome.Busy:
                Show(UpdateTexts.Busy, warning: false);
                break;
            default:
                Show(UpdateTexts.CheckFailed, warning: true);
                break;
        }
        Refresh();
    }

    private async Task ApplyAsync()
    {
        if (_updater is null)
            return;

        // ページを開いてから押すまでの間に、自動の確認が保留を取り下げる(release の撤回等)ことがある。
        // そのときは無反応にせず知らせて、ボタンを隠す。
        Refresh();
        if (PendingVersion is not { } version)
        {
            Show(UpdateTexts.NothingToApply, warning: false);
            return;
        }

        if (!_confirmApply(UpdateTexts.ApplyConfirm(version)))
            return;

        try
        {
            // 取得 → 取り込みを安全に止める → 適用して再起動(成功すれば戻らない)。
            await _updater.ApplyAndRestartAsync();
        }
        catch (Exception ex)
        {
            _logWarn($"更新の適用に失敗しました: {ex}");
            Show(UpdateTexts.ApplyFailed, warning: true);
        }
    }

    private void Show(string message, bool warning)
    {
        MessageIsWarning = warning;
        Message = message;
    }
}
