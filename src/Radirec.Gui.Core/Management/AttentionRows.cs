using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Core.Attention;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;

namespace Radirec.Gui.Core.Management;

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
    /// W-135。番組の設定と食い違ったままの回の 1 まとまり。放送局が変わる回(再適用では揃えない)と、パートだけが違う回は
    /// 形を変える。
    /// </summary>
    public static string DriftLine(SettingsDriftGroup group)
    {
        if (group.ToStation != group.Station)
            return $"{group.Station}「{group.FromAlbum}」→ {group.ToStation}「{group.ToAlbum}」 {group.Count} 回(放送局の変更は再適用しません)";
        if (group.ToAlbum == group.FromAlbum)
            return $"{group.Station}「{group.FromAlbum}」 パートの番号が変わる {group.Count} 回";
        return $"{group.Station}「{group.FromAlbum}」→「{group.ToAlbum}」 {group.Count} 回";
    }

    /// <summary>W-136(ボタン)。</summary>
    public const string ReapplyConfirm = "再適用の確認へ";

    /// <summary>W-119。取り込みが止まっている(W-103〜W-105)ので、W-136 を押せない。</summary>
    public const string ReapplyUnavailable = "取り込みが止まっているため、再適用を確かめられません";

    /// <summary>W-137。縮退(radirec.json を読めない)。</summary>
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
/// ホームの要対応が、計算と「統合する」「分けておく」をシェル(管理画面)に頼む面。ViewModel 同士をつなぐだけなので、
/// テストでは差し替える。
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

/// <summary>要対応の「再適用が要る番組」の 1 まとまり(W-135)。</summary>
public sealed class DriftAttentionRow(SettingsDriftGroup drift)
{
    public SettingsDriftGroup Drift { get; } = drift;

    /// <summary>W-135。</summary>
    public string Line { get; } = AttentionText.DriftLine(drift);
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
