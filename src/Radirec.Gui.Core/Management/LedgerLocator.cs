using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>
/// 管理画面の取込履歴とホームの要対応が読む台帳の場所(#196)。番組ページの programs.json の場所と同じ決め方で、
/// 取り込みワーカーがあれば今の状態(設定リロードの反映に追従する)から、無ければ起動時に読めた設定から取る。
/// 読むたびに呼んで決め直す(開いた時点の場所を持ち続けると、リロードで <c>LedgerPath</c> を変えたあとも古い台帳を読む)。
/// </summary>
public static class LedgerLocator
{
    /// <param name="control">取り込みワーカーの制御面。ワーカーが無い(W-104 / W-105)なら null。</param>
    /// <param name="startupLedgerPath">起動時に読めた設定の台帳のパス(解決済み)。設定を読めていない(W-105)なら null。</param>
    /// <returns>
    /// 台帳の場所を返す関数。設定を読めていない(場所が決まらない。W-150 / W-137)なら null。
    /// 状態を読めなければ、関数が例外を投げる(読む側が握って、読めなかった表示にする)。
    /// </returns>
    public static Func<string>? Create(IServiceControl? control, string? startupLedgerPath)
    {
        if (startupLedgerPath is null)
            return null;
        if (control is null)
            return () => startupLedgerPath;
        return () => control.GetStatus().LedgerPath is { Length: > 0 } live ? live : startupLedgerPath;
    }
}
