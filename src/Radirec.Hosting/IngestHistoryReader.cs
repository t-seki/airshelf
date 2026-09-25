using Radirec.Core.Ledger;

namespace Radirec.Hosting;

/// <summary>
/// 取込履歴を台帳から読み出す(S4 / #66)。トレイの管理画面と CLI の <c>radirec history</c>(#182)で共有する。
/// 取り込みワーカーの書込接続とは独立の **read-only 接続**を開いて列挙する(SQLite の WAL は 1 writer + 複数 reader を
/// 許容するため並行に安全)。
/// </summary>
public static class IngestHistoryReader
{
    /// <summary>台帳の記録を新しい順(取込日時の降順)に返す。台帳がまだ無い(取込ゼロ)ときは空で、ファイルは作らない。</summary>
    public static IReadOnlyList<LedgerEntry> Load(string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
            return [];

        using var ledger = new SqliteLedger(ledgerPath, readOnly: true);
        return ledger.ListAll();
    }
}
