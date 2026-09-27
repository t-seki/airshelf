using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Radirec.Gui.Core.Management;

/// <summary>
/// 管理画面の取込履歴ページ(#151)。台帳の全件を新しい順で出す。旧取込履歴ウィンドウ(S4 / #66・#87)の中身を
/// 引き継ぐ:列(W-141)・列の見出しでの並べ替え(ビューの DataGrid が行う)・右クリックのメニュー(W-144)・
/// 詳細欄(W-145)・「更新」(W-143)・件数(W-151)・読み込み失敗(W-149)。
/// <para>
/// 全件は管理画面を開いたときに 1 度読み、あとは「更新」でだけ読み直す(見ている最中に行がずれないように)。
/// 開く・コピーの結果は「更新」の横に 1 行で出す(<see cref="ActionMessage"/>)。
/// </para>
/// <para>
/// 台帳は UI スレッドの外で読む(NAS に届かないと SMB の時間切れまで返らず、その間画面が固まるため。#222)。
/// 最初の読み込みの間だけ W-154 を出し、読み直しの間は前の行と件数を出したままにする。読み込みの途中は「更新」を押せない
/// (時間切れを待つ読み込みを重ねない)。
/// </para>
/// </summary>
public sealed class IngestHistoryViewModel : ObservableObject
{
    /// <summary>W-146 の前半(開く操作の途中)。</summary>
    public const string Revealing = "エクスプローラーで開いています…";

    /// <summary>W-147 の 3 つ目(起動に失敗)。</summary>
    public const string RevealFailed = "エクスプローラーを開けませんでした(gui.log 参照)";

    /// <summary>W-148。</summary>
    public const string Copied = "パスをコピーしました";

    /// <summary>W-148。</summary>
    public const string CopyFailed = "パスをコピーできませんでした";

    private readonly Func<IReadOnlyList<IngestHistoryRow>>? _load;
    private readonly IHistoryPathActions _actions;
    private readonly Action<string> _logWarn;

    private IReadOnlyList<IngestHistoryRow> _rows = [];
    private HistoryLoadState _state = HistoryLoadState.Loading;
    private bool _loading;                    // 台帳を読んでいる(UI スレッドの外で)
    private IngestHistoryRow? _selected;
    private string _actionMessage = "";

    /// <param name="load">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="actions">エクスプローラーで開く / コピー(シェルの操作)。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    public IngestHistoryViewModel(Func<IReadOnlyList<IngestHistoryRow>>? load, IHistoryPathActions actions, Action<string> logWarn)
    {
        _load = load;
        _actions = actions;
        _logWarn = logWarn;

        RefreshCommand = new RelayCommand(Reload, () => _load is not null && !_loading);
        RevealOutputCommand = new AsyncRelayCommand(() => RevealAsync(output: true), () => HasOutput);
        RevealSourceCommand = new AsyncRelayCommand(() => RevealAsync(output: false), () => HasSource);
        CopyOutputCommand = new RelayCommand(() => Copy(output: true), () => HasOutput);
        CopySourceCommand = new RelayCommand(() => Copy(output: false), () => HasSource);

        Reload();
    }

    /// <summary>全件(新しい順)。</summary>
    public IReadOnlyList<IngestHistoryRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    public HistoryLoadState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsLoaded));
                OnPropertyChanged(nameof(IsUnavailable));
                OnPropertyChanged(nameof(IsAvailable));
            }
        }
    }

    /// <summary>台帳を読めた(一覧と詳細欄を出す)。</summary>
    public bool IsLoaded => State == HistoryLoadState.Loaded;

    /// <summary>縮退で台帳の場所が決まらない(W-150 だけを出す。「更新」も出さない)。</summary>
    public bool IsUnavailable => State == HistoryLoadState.Unavailable;

    /// <summary><see cref="IsUnavailable"/> の逆(「更新」と件数の行を出す。読み込みに失敗したときも読み直すために出す)。</summary>
    public bool IsAvailable => !IsUnavailable;

    /// <summary>「更新」の横:W-151 <c>{N} 件</c> か W-149 <c>読み込みに失敗しました</c>(最初の読み込みの途中は W-154)。</summary>
    public string CountText => State switch
    {
        HistoryLoadState.Loaded => HistoryText.Count(Rows.Count),
        HistoryLoadState.Failed => HistoryText.LoadFailed,
        HistoryLoadState.Loading => HistoryText.Loading,
        _ => "",
    };

    /// <summary>いま走っている(最後に始めた)読み込み。終わっていれば完了済みのタスク(テストで待つ)。</summary>
    public Task Reloaded { get; private set; } = Task.CompletedTask;

    /// <summary>選んでいる行(右クリックも、開く前にその行を選ぶ)。</summary>
    public IngestHistoryRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
                return;
            OnPropertyChanged(nameof(SelectedOutputPath));
            OnPropertyChanged(nameof(SelectedSourcePath));
            RevealOutputCommand.NotifyCanExecuteChanged();
            RevealSourceCommand.NotifyCanExecuteChanged();
            CopyOutputCommand.NotifyCanExecuteChanged();
            CopySourceCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>詳細欄の「出力先」(W-145)。</summary>
    public string SelectedOutputPath => SelectedRow?.OutputPath ?? "";

    /// <summary>詳細欄の「取込元」(W-145)。</summary>
    public string SelectedSourcePath => SelectedRow?.SourcePath ?? "";

    /// <summary>開く・コピーの結果(W-146〜W-148)。件数の表示は潰さない。</summary>
    public string ActionMessage { get => _actionMessage; private set => SetProperty(ref _actionMessage, value); }

    /// <summary>「更新」(W-143)。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>出力先をエクスプローラーで開く(W-144。行のダブルクリック・Enter も)。</summary>
    public AsyncRelayCommand RevealOutputCommand { get; }

    /// <summary>取込元をエクスプローラーで開く(W-144)。</summary>
    public AsyncRelayCommand RevealSourceCommand { get; }

    /// <summary>出力先のパスをコピー(W-144)。</summary>
    public RelayCommand CopyOutputCommand { get; }

    /// <summary>取込元のパスをコピー(W-144)。</summary>
    public RelayCommand CopySourceCommand { get; }

    // 台帳の source_path / output_path は NOT NULL で、取込パイプラインが必ず実パスを入れるため、通常は空にならない。
    // それでも空のままエクスプローラーを起こせば無関係な場所が開くので、防御として項目を伏せる(#87)。
    private bool HasOutput => !string.IsNullOrWhiteSpace(SelectedRow?.OutputPath);
    private bool HasSource => !string.IsNullOrWhiteSpace(SelectedRow?.SourcePath);

    private void Reload()
    {
        if (_load is null)
        {
            Rows = [];
            State = HistoryLoadState.Unavailable;
            OnPropertyChanged(nameof(CountText));
            return;
        }

        // 読み込みの途中は「更新」を押せないが、コマンドの外から呼ばれても重ねない(捨てる)。
        if (_loading)
            return;

        SetLoading(true);
        Reloaded = ReloadAsync(_load);
    }

    // 台帳は UI スレッドの外で読み、結果は呼んだスレッド(UI スレッド)で反映する(要対応の計算と同じ作り)。
    private async Task ReloadAsync(Func<IReadOnlyList<IngestHistoryRow>> load)
    {
        try
        {
            try
            {
                Rows = await Task.Run(load);
                State = HistoryLoadState.Loaded;
            }
            catch (Exception ex)
            {
                // 旧ウィンドウはメッセージボックスを出していたが、画面の中(件数の位置)に出す。理由は gui.log。
                _logWarn($"管理画面: 取込履歴を読めませんでした: {ex}");
                Rows = [];
                State = HistoryLoadState.Failed;
            }

            SelectedRow = null;
            ActionMessage = "";
            OnPropertyChanged(nameof(CountText));
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool loading)
    {
        _loading = loading;
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private async Task RevealAsync(bool output)
    {
        var row = SelectedRow;
        if (row is null)
            return;

        var path = output ? row.OutputPath : row.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ActionMessage = output ? "出力先が記録されていません" : "取込元が記録されていません"; // W-152
            return;
        }

        try
        {
            // 届かない NAS の存在確認は数秒かかりうる。待つ間も画面は操作できるので、途中であることを示す。
            ActionMessage = Revealing;
            ActionMessage = await _actions.RevealAsync(path);
        }
        catch (Exception ex)
        {
            // コマンドの例外を UI スレッドの未捕捉例外にしない(そちらは管理画面ごと閉じる最後の網)。
            _logWarn($"エクスプローラー起動でエラーが発生しました: {ex}");
            ActionMessage = RevealFailed;
        }
    }

    private void Copy(bool output)
    {
        var path = output ? SelectedRow?.OutputPath : SelectedRow?.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            ActionMessage = _actions.TryCopy(path) ? Copied : CopyFailed;
        }
        catch (Exception ex)
        {
            _logWarn($"クリップボードへのコピーに失敗しました: {ex}");
            ActionMessage = CopyFailed;
        }
    }
}
