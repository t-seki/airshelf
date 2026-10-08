using Airshelf.Hosting.Control;

namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 管理画面が読む台帳・stations.json・programs.json の場所(1 つの状態から決めた組)。
/// </summary>
/// <param name="LedgerPath">台帳のパス。</param>
/// <param name="StationsFile">stations.json のパス(解決済み)。空なら使わない設定。</param>
/// <param name="ProgramsFile">programs.json のパス(解決済み)。空なら使わない設定。</param>
public sealed record ManagementDataLocations(string LedgerPath, string? StationsFile, string? ProgramsFile);

/// <summary>
/// 管理画面の取込履歴とホームの要対応が読む台帳の場所(#196)。取り込みワーカーがあれば今の状態(設定リロードの反映に追従する)
/// から、無ければ起動時に読めた設定から取る。読むたびに呼んで決め直す(開いた時点の場所を持ち続けると、リロードで
/// <c>LedgerPath</c> を変えたあとも古い台帳を読む)。
/// <para>
/// 番組ページの programs.json の場所(<c>ProgramsFileLocator</c>)と違い、状態の <see cref="ServiceStatusSnapshot.LedgerPath"/>
/// が空なら起動時の台帳のパスへ戻る。programs.json / stations.json の空は「そのファイルを使わない設定」だが、台帳には
/// その意味が無く、空の状態(ワーカーが載せる前の状態など)で台帳の場所を失わないため。
/// </para>
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
        return () => Resolve(control?.GetStatus(), startupLedgerPath, null, null).LedgerPath;
    }

    /// <summary>
    /// 1 つの状態から、台帳・stations.json・programs.json の場所をまとめて決める。ワーカーは 3 つを 1 回の状態の差し替えで
    /// 変えるので、1 回の計算で使う場所は、同じ状態から取る(状態を 2 回読むと、そのあいだにリロードが終わって、古い設定と
    /// 新しい台帳を組み合わせうる)。
    /// </summary>
    /// <param name="status">ワーカーの今の状態。ワーカーが無ければ null(起動時の設定の場所を使う)。</param>
    public static ManagementDataLocations Resolve(
        ServiceStatusSnapshot? status, string startupLedgerPath, string? startupStationsFile, string? startupProgramsFile)
    {
        if (status is null)
            return new(startupLedgerPath, startupStationsFile, startupProgramsFile);
        var ledger = status.LedgerPath is { Length: > 0 } live ? live : startupLedgerPath;
        return new(ledger, status.StationsFile, status.ProgramsFile);
    }
}
