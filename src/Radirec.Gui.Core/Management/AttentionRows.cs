using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Core.Attention;
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
