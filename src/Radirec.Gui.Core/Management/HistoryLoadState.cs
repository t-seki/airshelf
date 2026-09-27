namespace Radirec.Gui.Core.Management;

/// <summary>取込履歴(ホームの数件・取込履歴ページ)を出せているか。</summary>
public enum HistoryLoadState
{
    /// <summary>台帳を読めた。</summary>
    Loaded,

    /// <summary>台帳を読めなかった(W-149 <c>読み込みに失敗しました</c>)。</summary>
    Failed,

    /// <summary>設定を読めず、台帳の場所が決まらない(W-150)。</summary>
    Unavailable,

    /// <summary>
    /// まだ一度も読み終えていない(W-154 <c>読み込み中…</c>)。台帳は UI スレッドの外で読む(NAS に届かないと SMB の
    /// 時間切れまで返らない。#222)。読み直しの間はこの状態にせず、前の行と状態を出したままにする。
    /// </summary>
    Loading,
}

/// <summary>取込履歴の文言(docs/mockups/management-window.md)。</summary>
public static class HistoryText
{
    /// <summary>W-149。</summary>
    public const string LoadFailed = "読み込みに失敗しました";

    /// <summary>W-150。</summary>
    public const string Unavailable = "設定を読めないため、取込履歴を出せません";

    /// <summary>W-154(最初の読み込みの途中)。</summary>
    public const string Loading = "読み込み中…";

    /// <summary>W-151(取込履歴ページの件数)。</summary>
    public static string Count(int count) => $"{count} 件";

    /// <summary>W-140(ホームの見出しの横)。</summary>
    public static string Recent(int count) => $"新しい方の {count} 件";
}

/// <summary>
/// 取込履歴の行から、出力先・取込元をエクスプローラーで開く / パスをコピーする(シェルの操作)。
/// 実装は Windows 依存なので <c>Radirec.Gui</c> にあり、ViewModel はこの面だけを見る(テストで差し替える)。
/// </summary>
public interface IHistoryPathActions
{
    /// <summary>エクスプローラーで開き、その結果の文言(W-146 / W-147)を返す。</summary>
    Task<string> RevealAsync(string path);

    /// <summary>クリップボードにコピーする。できなければ false(理由のログは実装が書く)。</summary>
    bool TryCopy(string path);
}
