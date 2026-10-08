using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Airshelf.Core.Attention;
using Airshelf.Core.Configuration;
using Airshelf.Core.Ledger;
using Airshelf.Core.Normalization;
using Airshelf.Core.Reapply;

namespace Airshelf.Gui.Core.Management;

/// <summary>要対応(ホーム)の文言(docs/mockups/management-window.md)。</summary>
public static class AttentionText
{
    /// <summary>W-120(件数)。</summary>
    public static string Count(int count) => $"要対応 {count} 件";

    /// <summary>W-121。</summary>
    public const string None = "要対応はありません";

    /// <summary>W-122(種類の見出し)。</summary>
    public const string SiblingsHeading = "統合設定の無い兄弟フォルダ";

    /// <summary>W-124。組の一部がすでに統合番組に入っている。</summary>
    public static string OutsideMerged(string album, IReadOnlyList<string> folders)
        => $"統合番組『{album}』に入っていないフォルダ{Quoted(folders)}があります";

    /// <summary>W-125。分けておく組に記録した組に、フォルダが加わった。</summary>
    public static string KeptApartGrown(IReadOnlyList<string> folders)
        => $"分けておく組に、新しいフォルダ{Quoted(folders)}が加わりました";

    /// <summary>W-128(種類の見出し)。</summary>
    public const string FailuresHeading = "解決していない取込失敗";

    /// <summary>W-129 の 3 行目。記録した時刻(ローカル)。</summary>
    public static string FailedAt(DateTimeOffset failedAt, TimeZoneInfo? zone)
        => $"失敗: {TimeZoneInfo.ConvertTime(failedAt, zone ?? TimeZoneInfo.Local):yyyy-MM-dd HH:mm}";

    /// <summary>W-130。理由を持つ前(#172 の移行前)に記録された失敗。</summary>
    public const string NoReason = "(理由の記録なし)";

    /// <summary>W-132(ボタン)。</summary>
    public const string RevealSource = "取込元をエクスプローラーで開く";

    /// <summary>W-133(W-128 の説明)。</summary>
    public const string FailuresHint = "録音ファイルを直すか置き直して、取り込みに成功すると消えます";

    /// <summary>W-134(種類の見出し)。</summary>
    public const string DriftHeading = "再適用が要る番組";

    /// <summary>
    /// W-135。番組の設定と食い違ったままの回の 1 まとまり。パートだけが違う回(アルバムは同じ)は 2 つ目の形にする。
    /// 放送局が変わる回は、再適用で揃えないので要対応に出さない(<see cref="AttentionItems.SettingsDrift"/>)。
    /// </summary>
    public static string DriftLine(SettingsDriftGroup group)
        => group.ToAlbum == group.FromAlbum
            ? $"{group.Station}「{group.FromAlbum}」 パートの番号が変わる {group.Count} 回"
            : $"{group.Station}「{group.FromAlbum}」→「{group.ToAlbum}」 {group.Count} 回";

    /// <summary>
    /// W-158。「取り込まない」と判断された取り込み済みの回の 1 まとまり(ADR-0021)。W-134 の中に W-135 と並べる(再適用で消す)。
    /// </summary>
    public static string RemovalLine(PendingRemovalGroup group)
        => $"{group.Station}「{group.Album}」 出力先から消す予定 {group.Count} 回(ファイル名の番組名「{group.FilenameProgram}」)";

    /// <summary>W-155(種類の見出し)。</summary>
    public const string SubstituteHeading = "差し替え放送の疑い";

    /// <summary>W-157(W-155 の説明)。</summary>
    public const string SubstituteHint =
        "ファイル名の番組名がフォルダ名を含まない回です。判断するまで、新しい回は取り込まずに保留します。" +
        "ボタンで番組ページの下書きに積み、番組ページで保存すると反映します";

    /// <summary>W-159(ボタン)。</summary>
    public const string StageSameProgram = "この番組の回";

    /// <summary>W-159(ボタン)。</summary>
    public const string StageSkip = "取り込まない";

    /// <summary>W-159(ボタン)。押すと、アルバム名をファイル名の番組名にして積む(番組ページで直す)。</summary>
    public const string StageOtherProgram = "別の番組…";

    /// <summary>W-160。その組に、保存していない判断が積まれている。</summary>
    public static string Staged(string treat) => $"下書き: {treat}";

    /// <summary>W-161。番組ページに、保存していない判断がある。</summary>
    public static string StagedSummary(int count)
        => $"番組ページに保存していない判断が {count} 件あります。番組ページで「確認」→「保存して適用」を押すと反映します";

    /// <summary>W-162(ボタン)。</summary>
    public const string ShowPrograms = "番組ページへ";

    /// <summary>W-156。差し替え放送の疑いの 1 組(ADR-0021)。数が 0 の側は出さない。</summary>
    public static string SuspectLine(SubstituteSuspectGroup group)
    {
        var counts = new List<string>();
        if (group.HeldCount > 0)
            counts.Add($"保留 {group.HeldCount} 回");
        if (group.ImportedCount > 0)
            counts.Add($"取り込み済み {group.ImportedCount} 回");
        return $"{group.Station}「{group.Folder}」に「{group.FilenameProgram}」 {string.Join(" / ", counts)}";
    }

    /// <summary>W-136(ボタン)。</summary>
    public const string ReapplyConfirm = "再適用の確認へ";

    /// <summary>W-119。取り込みが止まっている(W-103〜W-105・W-153)ので、W-136 を押せない。</summary>
    public const string ReapplyUnavailable = "取り込みが止まっているため、再適用を確かめられません";

    /// <summary>W-137。縮退(airshelf.json を読めない)。</summary>
    public const string Unavailable = "設定を読めないため、要対応を出せません";

    /// <summary>W-137 の 2 つ目。台帳か設定ファイルを読めず、要対応を計算できなかった。</summary>
    public static string ComputeFailed(string reason) => $"要対応を出せませんでした: {reason}";

    private static string Quoted(IReadOnlyList<string> folders) => string.Concat(folders.Select(f => $"『{f}』"));
}

/// <summary>要対応の「分けておく」の結果。</summary>
public enum KeepApartOutcome
{
    /// <summary>programs.json に書いた。</summary>
    Written,

    /// <summary>programs.json にコメントがあるので、その場では書かない(番組ページの下書きへ回す)。</summary>
    NeedsDraft,

    /// <summary>書けなかった(<see cref="KeepApartResult.Message"/> に理由)。</summary>
    Failed,
}

/// <summary>要対応の「分けておく」の結果と、出す文言(W-138 / W-139)。</summary>
public sealed record KeepApartResult(KeepApartOutcome Outcome, string? Message = null)
{
    public static KeepApartResult Written { get; } = new(KeepApartOutcome.Written);
    public static KeepApartResult NeedsDraft { get; } = new(KeepApartOutcome.NeedsDraft);
    public static KeepApartResult Failed(string message) => new(KeepApartOutcome.Failed, message);
}

/// <summary>
/// ホームの要対応が、計算と「統合する」「分けておく」、差し替え放送の判断(番組ページの下書きに積む・積んだ判断の表示と数・
/// 番組ページへ移る。W-159〜W-164。#235)をシェル(管理画面)に頼む面。ViewModel 同士をつなぐだけなので、テストでは差し替える。
/// </summary>
public interface IAttentionActions
{
    /// <summary>要対応を計算する(台帳とディスクの設定ファイルから)。読めなければ例外。</summary>
    AttentionItems Compute();

    /// <summary>「統合する」(W-126)。下書きを入れた番組ページへ移る。</summary>
    void Integrate(SiblingFolderGroup group);

    /// <summary>「分けておく」(W-127)。書けなかったら、その理由の文言を返す。書けた・下書きへ回したら null。</summary>
    Task<string?> KeepApartAsync(SiblingFolderGroup group);

    /// <summary>
    /// 「分けておく」をいま押せないなら、その理由(W-138。番組ページに保存していない変更がある)。押せるなら null。
    /// </summary>
    string? KeepApartBlockedReason { get; }

    /// <summary>
    /// 差し替え放送の疑いの 1 組に、判断を番組ページの下書きとして積む(W-159。ページは移らない)。積めたら null、
    /// 積めなかったら理由(W-163 / W-164)。
    /// </summary>
    Task<string?> StageDecisionAsync(SubstituteSuspectGroup group, FilenameProgramTreatment treat);

    /// <summary>その組に積まれている、保存していない判断の呼び名(W-160)。無ければ null。</summary>
    string? StagedDecisionLabel(SubstituteSuspectGroup group);

    /// <summary>番組ページの、保存していない判断の数(W-161)。</summary>
    int StagedDecisionCount { get; }

    /// <summary>「番組ページへ」(W-162)。</summary>
    void ShowPrograms();
}

/// <summary>要対応の「解決していない取込失敗」の 1 件(W-129〜W-132)。</summary>
public sealed class FailureAttentionRow
{
    public FailureAttentionRow(IngestFailureRecord failure, TimeZoneInfo? zone, Func<FailureAttentionRow, Task> reveal)
    {
        Failure = failure;
        FileName = FileNameOf(failure.SourcePath);
        Reason = string.IsNullOrWhiteSpace(failure.Reason) ? AttentionText.NoReason : failure.Reason;
        FailedAt = failure.FailedAtUtc is { } at ? AttentionText.FailedAt(at, zone) : null;
        RevealCommand = new AsyncRelayCommand(() => reveal(this));
    }

    public IngestFailureRecord Failure { get; }

    /// <summary>W-129 の 1 行目。</summary>
    public string FileName { get; }

    /// <summary>W-129 の 2 行目。理由の記録が無ければ W-130。</summary>
    public string Reason { get; }

    /// <summary>W-129 の 3 行目。日時の記録が無い(#172 の移行前の)失敗は null(この行を出さない)。</summary>
    public string? FailedAt { get; }

    public bool HasFailedAt => FailedAt is not null;

    /// <summary>W-132。取込元から消えた失敗は走査が記録を消す(#172)ので、いつでも押せる。</summary>
    public IAsyncRelayCommand RevealCommand { get; }

    // 台帳のパスは Windows の形(\ 区切り)。WSL のテストでも同じに切れるよう、両方の区切りで切る。
    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }
}

/// <summary>
/// 要対応の「再適用が要る番組」の 1 まとまり(W-135)。「取り込まない」と判断された回の「出力先から消す予定」(W-158。ADR-0021)も
/// 同じ欄に並べる。
/// </summary>
public sealed class DriftAttentionRow
{
    public DriftAttentionRow(SettingsDriftGroup drift)
    {
        Key = drift;
        Line = AttentionText.DriftLine(drift);
    }

    public DriftAttentionRow(PendingRemovalGroup removal)
    {
        Key = removal;
        Line = AttentionText.RemovalLine(removal);
    }

    /// <summary>元のまとまり(<see cref="SettingsDriftGroup"/> か <see cref="PendingRemovalGroup"/>。中身が変わったかの比較に使う)。</summary>
    public object Key { get; }

    /// <summary>食い違いのまとまり(W-135)。消す予定のまとまり(W-158)なら null。</summary>
    public SettingsDriftGroup? Drift => Key as SettingsDriftGroup;

    /// <summary>W-135 / W-158。</summary>
    public string Line { get; }
}

/// <summary>
/// 要対応の「差し替え放送の疑い」の 1 組(W-156。ADR-0021)。判断のボタン(W-159)で番組ページの下書きに積み、積んだ判断を
/// W-160 で見せる(#235)。
/// </summary>
public sealed class SubstituteAttentionRow : ObservableObject
{
    private string? _staged;

    public SubstituteAttentionRow(SubstituteSuspectGroup suspect, Func<SubstituteAttentionRow, FilenameProgramTreatment, Task> stage)
    {
        Suspect = suspect;
        Line = AttentionText.SuspectLine(suspect);
        SameProgramCommand = new AsyncRelayCommand(() => stage(this, FilenameProgramTreatment.SameProgram));
        SkipCommand = new AsyncRelayCommand(() => stage(this, FilenameProgramTreatment.Skip));
        OtherProgramCommand = new AsyncRelayCommand(() => stage(this, FilenameProgramTreatment.OtherProgram));
    }

    public SubstituteSuspectGroup Suspect { get; }

    /// <summary>W-156。</summary>
    public string Line { get; }

    /// <summary>W-160(積んだ判断)。積んでいなければ null。</summary>
    public string? Staged
    {
        get => _staged;
        set
        {
            if (SetProperty(ref _staged, value))
                OnPropertyChanged(nameof(IsStaged));
        }
    }

    public bool IsStaged => Staged is not null;

    /// <summary>W-159「この番組の回」。</summary>
    public IAsyncRelayCommand SameProgramCommand { get; }

    /// <summary>W-159「取り込まない」。</summary>
    public IAsyncRelayCommand SkipCommand { get; }

    /// <summary>W-159「別の番組…」。</summary>
    public IAsyncRelayCommand OtherProgramCommand { get; }
}

/// <summary>要対応の「統合設定の無い兄弟フォルダ」の 1 組(W-123〜W-127)。</summary>
public sealed class SiblingAttentionRow : ObservableObject
{
    private bool _canKeepApart = true;

    public SiblingAttentionRow(UnintegratedSiblingGroup sibling, Action<SiblingAttentionRow> integrate, Func<SiblingAttentionRow, Task> keepApart)
    {
        Sibling = sibling;
        Line = ReapplyDialogFormatter.SiblingLine(sibling);
        Heading = sibling.IsKeptApartGrown
            ? AttentionText.KeptApartGrown(sibling.FoldersNewToKeptApart)
            : sibling.MergedAlbum is { } album
                ? AttentionText.OutsideMerged(album, sibling.FoldersWithoutSetting)
                : null;
        IntegrateCommand = new RelayCommand(() => integrate(this));
        KeepApartCommand = new AsyncRelayCommand(() => keepApart(this), () => CanKeepApart);
    }

    public UnintegratedSiblingGroup Sibling { get; }

    /// <summary>W-124 / W-125。どちらでもなければ null(W-123 の行だけ)。</summary>
    public string? Heading { get; }

    public bool HasHeading => Heading is not null;

    /// <summary>W-123。</summary>
    public string Line { get; }

    /// <summary>「分けておく」を押せるか(番組ページに保存していない変更があれば押せない。W-138)。</summary>
    public bool CanKeepApart
    {
        get => _canKeepApart;
        set
        {
            if (SetProperty(ref _canKeepApart, value))
                KeepApartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>W-126。</summary>
    public IRelayCommand IntegrateCommand { get; }

    /// <summary>W-127。</summary>
    public IAsyncRelayCommand KeepApartCommand { get; }
}
