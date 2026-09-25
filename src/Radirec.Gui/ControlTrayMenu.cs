using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Updates;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Gui;

/// <summary>
/// トレイの制御メニュー(ADR-0012)。rescan / status / reload / 統合設定の再適用(#126)を、同一プロセスでホストする
/// 取り込みワーカーの <see cref="IServiceControl"/> を**直接 in-process 呼び出し**で叩く(named pipe は退役)。
/// status はメニューを開いた瞬間(<see cref="ToolStripDropDown.Opening"/>)にスナップショットを読んで表示を
/// 更新する。取り込みワーカー未起動(設定未読込 = <see cref="IServiceControl"/> が null)のときは
/// 制御の項目(rescan / reload / 再適用)をすべてグレーアウトし「取り込み停止中」を表示する縮退にする。
/// 再適用の確認〜完了表示の途中も、ダイアログが入れ子にならないよう制御の項目をすべてグレーアウトする。
/// 再適用の確認ダイアログに統合設定の無い兄弟フォルダがあれば、その提案を programs.json に追記して開ける(#144)。
/// 「設定ファイルを開く」(#143)は制御の項目ではなく、取り込みの状態(停止中・再適用の途中)に依らず常に選べる
/// (停止の原因の設定を直してから再起動・リロードするために要る。開くだけでワーカーには触れない)。その下の
/// programs.json / stations.json だけは、場所が決まらないとき(設定を読み込めていない、または radirec.json の
/// ProgramsFile / StationsFile が空でそのファイルを使わない)にグレーアウトし、理由をツールチップに出す。
/// 「Radirec を開く」(#151)は管理画面を開く既定の項目(太字。アイコンのダブルクリックでも開く)で、縮退時も選べる。
/// 取込履歴は管理画面の取込履歴ページへ移ったので、トレイには置かない。
/// </summary>
internal sealed class ControlTrayMenu
{
    private readonly NotifyIcon _tray;
    private readonly IServiceControl? _control;
    private readonly string? _degradedReason;
    private readonly UpdateService? _update;
    private readonly Func<Task>? _drainWorker; // 更新適用前にワーカーを graceful 停止(S6 レビュー #0)
    private readonly string? _configPath;           // 読み込んだ(読もうとした)radirec.json。null なら「設定ファイルを開く」を出さない
    private readonly RadirecConfig? _startupConfig; // 起動時に読めた設定。ワーカーが無いときの開く場所の出どころ(#143)
    private readonly ToolStripMenuItem _rescan;
    private readonly ToolStripMenuItem _reload;
    private readonly ToolStripMenuItem _reapply;
    private readonly ToolStripMenuItem? _openConfig; // 「設定ファイルを開く」のサブメニュー(#143)
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _lastScanLine;
    private readonly ToolStripMenuItem? _applyUpdate; // 更新保留時のみ表示(S6)
    private readonly ToolStripSeparator? _updateSeparator; // _applyUpdate の上の区切り線。同じく保留時のみ(#157)
    private bool _reapplyBusy; // 再適用の確認〜完了表示の間は制御の項目を伏せる(UI スレッドだけが読み書きする)

    /// <param name="openManagement">管理画面を開く(#151)。「Radirec を開く」で呼ぶ。</param>
    /// <param name="control">取り込みワーカーの制御面。縮退(未起動)時は null。</param>
    /// <param name="degradedReason">control==null の理由(縮退メニューに表示)。healthy 時は null。</param>
    /// <param name="update">自己更新サービス(S6)。null / 未インストール(dev)なら更新項目を出さない。</param>
    /// <param name="drainWorker">更新適用直前にワーカーを graceful 停止するコールバック(S6)。</param>
    /// <param name="configPath">radirec.json の絶対パス(#143)。読めなかったときも渡す(直すために開く)。</param>
    /// <param name="startupConfig">起動時に読めた設定(#143)。読めなかったときは null。</param>
    public ControlTrayMenu(
        NotifyIcon tray, Action openManagement, IServiceControl? control, string? degradedReason = null,
        UpdateService? update = null, Func<Task>? drainWorker = null,
        string? configPath = null, RadirecConfig? startupConfig = null)
    {
        _tray = tray;
        _control = control;
        _degradedReason = degradedReason;
        _update = update?.IsInstalled == true ? update : null; // dev/portable では更新 UI を出さない
        _drainWorker = drainWorker;
        _configPath = configPath;
        _startupConfig = startupConfig;

        _rescan = new ToolStripMenuItem("今すぐ再スキャン", image: null, OnRescan);
        _reload = new ToolStripMenuItem("設定リロード…", image: null, OnReload);
        _reapply = new ToolStripMenuItem("統合設定の再適用…", image: null, OnReapply);

        // 設定ファイルを開く(#143)。開いて直す → 保存 → 「設定リロード…」か「統合設定の再適用…」の流れなので、その隣に置く。
        // 項目の有効/無効とツールチップ(開く場所)は Opening で更新する(RefreshConfigFileItems)。
        if (_configPath is not null)
        {
            _openConfig = new ToolStripMenuItem("設定ファイルを開く");
            _openConfig.DropDown.ShowItemToolTips = true; // 開く場所(フルパス)をツールチップで見せる
            foreach (var (file, label) in ConfigFileLabels)
                _openConfig.DropDownItems.Add(new ToolStripMenuItem(label, image: null, OnOpenConfig) { Tag = file });
        }

        // 状態表示は非活性のラベル項目(クリック不可)。Opening で内容を差し替える。
        _statusLine = new ToolStripMenuItem("状態: 未確認") { Enabled = false };
        _lastScanLine = new ToolStripMenuItem("最終スキャン: —") { Enabled = false };

        // 版情報(S1 / #63)。クリック不可のヘッダー。CI がタグから注入した版、ローカルは 0.0.0-dev。
        var versionLine = new ToolStripMenuItem($"Radirec {AppVersion.Of(typeof(ControlTrayMenu).Assembly)}")
        {
            Enabled = false,
        };

        Menu = new ContextMenuStrip();

        // 管理画面を開く(#151・W-602)。既定の項目なので太字にする(アイコンのダブルクリックでも開く。Program.cs)。
        // 縮退時も選べる(管理画面は縮退の状態を見せる)。
        var open = new ToolStripMenuItem("Radirec を開く", image: null, (_, _) => openManagement());
        open.Font = new Font(open.Font, FontStyle.Bold);

        Menu.Items.Add(versionLine);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(open); // モックの並び(W-601 / 区切り / W-602 / W-603)
        Menu.Items.Add(_rescan);
        Menu.Items.Add(_reload);
        Menu.Items.Add(_reapply);
        if (_openConfig is not null)
            Menu.Items.Add(_openConfig);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(_statusLine);
        Menu.Items.Add(_lastScanLine);
        // 更新(S6 / #68)。Velopack インストール時のみ。適用項目(W-605)は保留中だけ、上の区切り線ごと表示する。
        // 「今すぐ更新を確認」は管理画面のバージョン情報ページへ移した(#157)。
        if (_update is not null)
        {
            _updateSeparator = new ToolStripSeparator { Visible = false };
            Menu.Items.Add(_updateSeparator);
            _applyUpdate = new ToolStripMenuItem("更新を適用して再起動", image: null, OnApplyUpdate) { Visible = false };
            Menu.Items.Add(_applyUpdate);
        }
        Menu.Items.Add(new ToolStripSeparator()); // 直前は最終スキャンの行か更新の項目(区切り線にはならない)
        // 縮退(設定未読込 / ワーカー起動失敗)時は、設定修正後にプロセスごと再起動して復帰できる導線を出す
        // (control==null は復旧経路が無く、再起動しないと取り込みが始まらないため)。
        if (_control is null)
            Menu.Items.Add("再読み込みして再起動", image: null, (_, _) => Application.Restart());
        Menu.Items.Add("終了", image: null, (_, _) => Application.Exit());

        Menu.Opening += OnOpening;
    }

    public ContextMenuStrip Menu { get; }

    private void OnOpening(object? sender, CancelEventArgs e)
    {
        RefreshUpdateItem(); // 直近のチェックで見つかった保留更新を反映(S6)
        RefreshConfigFileItems(); // 取り込みの状態(停止中・再適用の途中)に依らず更新する(#143)

        // 取り込みワーカー未起動(設定未読込 or 起動失敗)なら理由を、あれば in-process のスナップショットを
        // 直接・即時に読んで出す(W-101〜W-108。文言は管理画面のホームと共有する)。ワーカーが異常終了しても
        // トレイ(メッセージループ)は生存しうるが、その場合は「稼働中」と偽らず停止を出す(ADR-0012)。
        var view = _control is null
            ? ServiceStatusText.Stopped(_degradedReason)
            : ServiceStatusText.From(_control.GetStatus());
        _statusLine.Text = view.Status;
        _lastScanLine.Text = view.LastScan;

        // 取り込みが止まっていれば制御の項目(rescan / reload / 再適用)を伏せる。再適用の確認〜完了の途中も、
        // 2 本目の再適用も、再スキャン・設定リロード(確認の MessageBox が再適用のダイアログと入れ子になる・
        // 確認した設定を途中で差し替える)も始めない。
        var enabled = view.CanControl && !_reapplyBusy;
        _rescan.Enabled = enabled;
        _reload.Enabled = enabled;
        _reapply.Enabled = enabled;
    }

    private void OnRescan(object? sender, EventArgs e)
    {
        if (_reapplyBusy) // 項目は伏せてあるが、念のため(再適用の途中は受けない)
            return;

        // 要求チャネルへ coalesce される冪等操作。即座に返る(スキャン完了は待たない)。
        _control?.RequestRescan();
        _tray.ShowBalloonTip(3000, "Radirec", "手動再スキャンを要求しました", ToolTipIcon.Info);
    }

    private void OnReload(object? sender, EventArgs e)
    {
        if (_control is null || _reapplyBusy) // 再適用の途中は受けない(項目も伏せてある)
            return;

        var confirm = MessageBox.Show(
            "設定を再読み込みします。取り込みは一時停止し、再構築されます。よろしいですか?",
            "設定リロード",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
            return;

        // 読込+構文検証だけ同期実行して受理/拒否が即返る(実グラフ差し替えは在中スキャン完了後)。
        var outcome = _control.RequestReload();
        if (outcome.Accepted)
            _tray.ShowBalloonTip(3000, "Radirec", "設定リロードを受理しました(反映は取込完了後)", ToolTipIcon.Info);
        else
            _tray.ShowBalloonTip(5000, "Radirec", $"設定リロードは拒否されました: {outcome.Error}", ToolTipIcon.Warning);
    }

    // 統合設定の再適用(#126 / ADR-0016)。設定リロード → dry-run → 確認([適用] / [キャンセル])→ 実行 → 完了表示。
    // 統合設定の無い兄弟フォルダがあれば、確認(「揃っています」の表示も)に [統合設定を追記して開く] も出す(#144)。
    // それを選ぶと提案を programs.json に追記して開き、その回の再適用は行わずに終わる(追記の確認で [キャンセル] したら
    // 元の確認に戻る)。
    // リロード・dry-run・実行は worker の直列ループの上で行う(在中スキャンの完了を待つ)。UI スレッドは await で待ち、
    // その間メニューは生きている(制御の項目だけ伏せる)。
    // 再適用の確認(設定リロード + dry-run)がこれより長くかかるときだけ「確かめています」のバルーンを出す。
    // ふだんはすぐ終わるので出さず、取り込み中の走査を待つときや NAS が遅いときだけ知らせる(実機確認 v0.11.0-rc1)。
    private static readonly TimeSpan ReapplyBalloonDelay = TimeSpan.FromMilliseconds(1500);

    private async void OnReapply(object? sender, EventArgs e)
    {
        if (_control is null || _reapplyBusy)
            return;

        _reapplyBusy = true;
        try
        {
            // すぐ終わるなら確認ダイアログとほぼ同時にバルーンが出て冗長なので、少し待っても終わらないときだけ知らせる。
            var planTask = _control.PlanReapplyAsync();
            if (await Task.WhenAny(planTask, Task.Delay(ReapplyBalloonDelay)) != planTask)
                _tray.ShowBalloonTip(3000, "Radirec", "設定を読み込み、再適用の内容を確かめています(取り込み中なら完了後)", ToolTipIcon.Info);
            var planned = await planTask; // 例外で終わっていればここで投げ、下の catch に届く
            if (planned.Report is not { } plan)
            {
                ShowReapplyDialog(new ReapplyDialogText("統合設定の再適用を中止しました", planned.Error ?? "理由不明"), TaskDialogIcon.Error);
                return;
            }

            // 統合設定の無い兄弟フォルダがあれば、提案を programs.json に追記して開くボタンも出す(#144)。
            // programs.json を使わない設定(ProgramsFile が空)なら追記先が無いので出さない。
            var canAppend = plan.UnintegratedSiblings.Count > 0 && LocateConfigFile(ConfigFile.Programs) is not null;
            while (true)
            {
                // 変える回が無ければ確認せずに終わる(CLI と同じ判定)。
                var choice = plan.IsAligned
                    ? ShowAligned(ReapplyDialogFormatter.BuildAligned(plan, canAppend), canAppend)
                    : ConfirmReapply(ReapplyDialogFormatter.BuildConfirmation(plan, canAppend), canAppend);

                // 追記を選んだら、その回の再適用は行わない(利用者が programs.json を確かめて保存してから選び直す)。
                // 追記の確認でキャンセルしたときだけ、元のダイアログに戻る。
                if (choice == ReapplyChoice.Append)
                {
                    if (await AppendSiblingProposalsAsync(plan))
                        return;
                    continue;
                }

                if (choice != ReapplyChoice.Apply)
                    return;
                break;
            }

            _tray.ShowBalloonTip(3000, "Radirec", "統合設定の再適用を実行しています", ToolTipIcon.Info);
            var applied = await _control.ApplyReapplyAsync(plan);
            if (applied.Report is not { } result)
            {
                ShowReapplyDialog(new ReapplyDialogText("統合設定の再適用を中止しました", applied.Error ?? "理由不明"), TaskDialogIcon.Error);
                return;
            }

            var icon = result.Count(ReapplyAction.Failed) > 0 ? TaskDialogIcon.Warning : TaskDialogIcon.Information;
            ShowReapplyDialog(ReapplyDialogFormatter.BuildCompletion(result), icon);
        }
        catch (Exception ex)
        {
            // async void の例外はメッセージループへ抜けてトレイごと落とすので、ここで止める。
            TrayLog.Warn($"統合設定の再適用で例外: {ex}");
            _tray.ShowBalloonTip(4000, "Radirec", "統合設定の再適用に失敗しました(gui.log 参照)", ToolTipIcon.Warning);
        }
        finally
        {
            _reapplyBusy = false;
        }
    }

    // 再適用の確認ダイアログで選ばれたもの。
    private enum ReapplyChoice
    {
        Apply,  // [適用]
        Append, // [統合設定を追記して開く](#144)
        Close,  // [キャンセル] / [OK] / 閉じた
    }

    // [適用] / [キャンセル] の確認。canAppend のときは [適用] / [統合設定を追記して開く] / [キャンセル] の 3 つ(#144)。
    // 追記を選んだら呼び出し元は再適用をせず追記へ進む。ファイルを動かす操作なので既定はキャンセル(設定リロードの確認と同じ)。
    // MessageBox はボタンの文言を変えられないので TaskDialog を使う。
    private static ReapplyChoice ConfirmReapply(ReapplyDialogText text, bool canAppend)
    {
        var apply = new TaskDialogButton("適用");
        var append = new TaskDialogButton(ReapplyDialogFormatter.AppendButton);
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "統合設定の再適用",
            Heading = text.Heading,
            Text = text.Body,
            Icon = TaskDialogIcon.Warning,
        };
        page.Buttons.Add(apply);
        if (canAppend)
            page.Buttons.Add(append);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;

        var chosen = TaskDialog.ShowDialog(page);
        return chosen == apply ? ReapplyChoice.Apply : chosen == append ? ReapplyChoice.Append : ReapplyChoice.Close;
    }

    // 揃っているときの表示([OK] だけ。統合設定の無い兄弟フォルダがあれば [統合設定を追記して開く] も)。
    private static ReapplyChoice ShowAligned(ReapplyDialogText text, bool canAppend)
    {
        var append = new TaskDialogButton(ReapplyDialogFormatter.AppendButton);
        var ok = TaskDialogButton.OK;
        var page = new TaskDialogPage
        {
            Caption = "統合設定の再適用",
            Heading = text.Heading,
            Text = text.Body,
            Icon = TaskDialogIcon.Information,
        };
        if (canAppend)
            page.Buttons.Add(append);
        page.Buttons.Add(ok);
        page.DefaultButton = ok; // Enter で追記に進まないように(ボタンを足す前と同じく、Enter は閉じる)
        return TaskDialog.ShowDialog(page) == append ? ReapplyChoice.Append : ReapplyChoice.Close;
    }

    // 統合設定の無い兄弟フォルダの提案を programs.json に追記して開く(#144)。追記する中身を見せて [追記] / [キャンセル]
    // → バックアップを作って追記 → エディタで開く → 保存してから再適用を選び直すようバルーンで案内する。
    // programs.json が無ければ、先に起動時と同じ見本から作ってから同じ流れに進む(作れなければエラーで終わる)。
    // 追記できない(壊れている・"Programs" の配列が無い・確認の間に変わった等)ときは何も書かずにエラーを出し、ファイルだけ開く。
    // ファイルの読み書きはワーカースレッドで行う(ProgramsFile が NAS を指していても UI スレッドを塞がない)。
    // 戻り値: 追記の確認で [キャンセル] したときだけ false(呼び出し元は再適用の確認ダイアログに戻る)。
    private async Task<bool> AppendSiblingProposalsAsync(ReapplyReport plan)
    {
        if (LocateConfigFile(ConfigFile.Programs) is not { } path)
            return true; // ボタンは出していない(programs.json を使わない設定)

        var name = Path.GetFileName(path);
        var groups = plan.UnintegratedSiblings.Select(s => s.Group).ToList();

        // programs.json が無ければ、起動時(「設定ファイルを開く」も)と同じ見本から作ってから追記する。
        // 作れなければ追記も開くこともできないので、エラーを出して終わる(理由は gui.log)。
        var (existed, ready) = await Task.Run(() =>
        {
            var exists = File.Exists(path);
            return (exists, exists || ConfigSeeder.SeedFileIfAbsent(ConfigFile.Programs, path));
        });
        if (!ready)
        {
            ShowReapplyDialog(ReapplyDialogFormatter.BuildSeedFailed(name), TaskDialogIcon.Error);
            return true;
        }

        ProgramsAppendPlan append;
        try
        {
            append = await Task.Run(() => ProgramsFileAppender.Plan(path, groups, DateOnly.FromDateTime(DateTime.Now)));
        }
        catch (ProgramsAppendException ex)
        {
            await ShowAppendFailedAndOpenAsync(path, ex.Message, ex, seeded: !existed);
            return true;
        }

        if (append.Proposals.Count == 0)
        {
            ShowReapplyDialog(ReapplyDialogFormatter.BuildNothingToAppend(name), TaskDialogIcon.Information);
            await OpenProgramsFileAsync(path, null);
            return true;
        }

        if (!ConfirmAppend(ReapplyDialogFormatter.BuildAppendConfirmation(append.Proposals, name), append.Snippet))
            return false;

        string backup;
        try
        {
            backup = await Task.Run(() => ProgramsFileAppender.Apply(append, DateTime.Now));
        }
        catch (ProgramsAppendException ex)
        {
            await ShowAppendFailedAndOpenAsync(path, ex.Message, ex, seeded: !existed);
            return true;
        }
        catch (Exception ex)
        {
            // バックアップか置き換えで失敗した。置き換えは一時ファイル経由なので programs.json は元のまま。
            await ShowAppendFailedAndOpenAsync(path, $"書き込みに失敗しました: {ex.Message}", ex, seeded: !existed);
            return true;
        }

        TrayLog.Info($"{name} に統合設定の提案を {append.Proposals.Count} 組追記しました({path}。バックアップ: {backup}):\n{append.Snippet}");
        await OpenProgramsFileAsync(path, ReapplyDialogFormatter.AppendedBalloon(name, seeded: !existed));
        return true;
    }

    // seeded: 追記の前に見本から programs.json を作ったか(このときは「何も書き込んでいない」と言わない)。
    private async Task ShowAppendFailedAndOpenAsync(string path, string reason, Exception ex, bool seeded)
    {
        TrayLog.Warn($"{Path.GetFileName(path)} に統合設定の提案を追記できませんでした({path}): {ex}");
        ShowReapplyDialog(ReapplyDialogFormatter.BuildAppendFailed(Path.GetFileName(path), reason, seeded), TaskDialogIcon.Error);
        await OpenProgramsFileAsync(path, null);
    }

    // programs.json を開く。開けたら balloon(null なら開いたときの知らせ)を、開けなければその理由をバルーンで出す。
    private async Task OpenProgramsFileAsync(string path, string? balloon)
    {
        var opened = await ConfigFileOpener.OpenAsync(ConfigFile.Programs, path);
        if (!opened.Ok)
            _tray.ShowBalloonTip(4000, "Radirec", opened.Message ?? $"{Path.GetFileName(path)} を開けませんでした", ToolTipIcon.Warning);
        else if ((balloon ?? opened.Message) is { } message)
            _tray.ShowBalloonTip(5000, "Radirec", message, ToolTipIcon.Info);
    }

    // 追記の確認([追記] / [キャンセル])。追記する中身は「詳細」に開いた状態で見せる(長ければ先頭だけ。ダイアログは
    // スクロールしないので、画面からはみ出して [追記] に届かなくならないように)。既定はキャンセル(ファイルを書き換えるため)。
    private static bool ConfirmAppend(ReapplyDialogText text, string snippet)
    {
        var append = new TaskDialogButton("追記");
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "統合設定の再適用",
            Heading = text.Heading,
            Text = text.Body,
            Icon = TaskDialogIcon.Information,
            Buttons = { append, cancel },
            DefaultButton = cancel,
            Expander = new TaskDialogExpander(ReapplyDialogFormatter.SnippetPreview(snippet))
            {
                Expanded = true,
                CollapsedButtonText = "詳細(追記する中身)",
                ExpandedButtonText = "詳細を隠す",
                Position = TaskDialogExpanderPosition.AfterText,
            },
        };
        return TaskDialog.ShowDialog(page) == append;
    }

    private static void ShowReapplyDialog(ReapplyDialogText text, TaskDialogIcon icon)
    {
        var page = new TaskDialogPage
        {
            Caption = "統合設定の再適用",
            Heading = text.Heading,
            Text = text.Body,
            Icon = icon,
            Buttons = { TaskDialogButton.OK },
        };
        TaskDialog.ShowDialog(page);
    }

    // 「設定ファイルを開く」の項目の並び(#143)。見出しは既定のファイル名(StationsFile 等で名前を変えていても。場所はツールチップ)。
    private static readonly (ConfigFile File, string Label)[] ConfigFileLabels =
    [
        (ConfigFile.Radirec, "radirec.json"),
        (ConfigFile.Programs, "programs.json"),
        (ConfigFile.Stations, "stations.json"),
    ];

    // 開く場所 = 今読み込んでいる設定から計算した場所。ワーカーがあれば status(設定リロードの反映に追従する)、
    // 無ければ起動時に読めた設定から。どちらも無い(設定を読めていない)なら radirec.json 以外は決まらず null。
    private string? LocateConfigFile(ConfigFile file)
    {
        if (_configPath is null)
            return null;

        if (_control is not null)
        {
            var status = _control.GetStatus();
            return ConfigPaths.Locate(file, _configPath, status.StationsFile, status.ProgramsFile);
        }
        return ConfigPaths.Locate(file, _configPath, _startupConfig?.StationsFile, _startupConfig?.ProgramsFile);
    }

    // 設定を読めているか(ワーカーがある = 読めた設定で動いている、または起動時に読めた)。
    private bool ConfigLoaded => _control is not null || _startupConfig is not null;

    private void RefreshConfigFileItems()
    {
        if (_openConfig is null)
            return;

        foreach (ToolStripItem item in _openConfig.DropDownItems)
        {
            if (item.Tag is not ConfigFile file)
                continue;
            var path = LocateConfigFile(file);
            item.Enabled = path is not null;
            // 場所が決まらない理由は 2 つ。設定を読めていないか、読めた設定でそのファイルを使わない(パスが空)か。
            item.ToolTipText = path
                ?? (ConfigLoaded
                    ? "radirec.json でこのファイルを使わない設定になっています"
                    : "設定を読み込めていないため、場所が分かりません");
        }
    }

    private async void OnOpenConfig(object? sender, EventArgs e)
    {
        if (sender is not ToolStripItem { Tag: ConfigFile file })
            return;

        try
        {
            if (LocateConfigFile(file) is not { } path)
                return; // 項目は伏せてある(設定を読み込めていない)

            var result = await ConfigFileOpener.OpenAsync(file, path);
            if (result.Message is { } message)
                _tray.ShowBalloonTip(result.Ok ? 3000 : 4000, "Radirec", message, result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            // async void の例外はメッセージループへ抜けてトレイごと落とすので、ここで止める。
            TrayLog.Warn($"設定ファイルを開くときに例外: {ex}");
            _tray.ShowBalloonTip(4000, "Radirec", "設定ファイルを開けませんでした(gui.log 参照)", ToolTipIcon.Warning);
        }
    }

    private async void OnApplyUpdate(object? sender, EventArgs e)
    {
        if (_update is null)
            return;

        // メニューを開いてから確定までの間に、自動チェックが保留更新を取り下げる(release 撤回等)ことがある。
        // その場合は無反応にせず知らせて項目を隠す。
        var version = _update.PendingVersion;
        if (version is null)
        {
            _tray.ShowBalloonTip(3000, "Radirec", UpdateTexts.NothingToApply, ToolTipIcon.Info); // W-509
            RefreshUpdateItem();
            return;
        }

        // W-508。既定はキャンセル側(再起動を Enter の押し間違いで始めない。管理画面の確認と揃える。#157 の設計の決定 3)。
        var confirm = MessageBox.Show(
            UpdateTexts.ApplyConfirm(version),
            UpdateTexts.ApplyConfirmCaption,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
            return;

        try
        {
            // DL → ワーカーを graceful 停止(在中スキャン完了・SQLite クローズ)→ 適用・再起動(戻らない)。
            await _update.ApplyAndRestartAsync(_drainWorker);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"更新の適用に失敗しました: {ex}");
            _tray.ShowBalloonTip(4000, "Radirec", UpdateTexts.ApplyFailed, ToolTipIcon.Warning); // W-509
        }
    }

    // 保留中の更新があれば「更新 vX を適用…」項目(W-605)を上の区切り線ごと出す。無ければ隠す。Opening で呼ぶ。
    private void RefreshUpdateItem()
    {
        if (_applyUpdate is null || _update is null)
            return;

        var version = _update.PendingVersion;
        if (version is not null)
            _applyUpdate.Text = UpdateTexts.ApplyButton(version);
        _applyUpdate.Visible = version is not null;
        if (_updateSeparator is not null)
            _updateSeparator.Visible = version is not null;
    }
}
