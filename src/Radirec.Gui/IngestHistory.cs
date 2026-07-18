using System.IO;
using System.Runtime.Versioning;
using Radirec.Core.Ledger;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// 取込履歴を台帳から読み出す(S4 / #66)。取り込みワーカーの書込接続とは独立の **read-only 接続**を
/// 開いて列挙する(SQLite の WAL は 1 writer + 複数 reader を許容するため並行に安全)。台帳がまだ無い
/// (取込ゼロ)ときは空を返す。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IngestHistory
{
    public static IReadOnlyList<IngestHistoryRow> Load(string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
            return [];

        using var ledger = new SqliteLedger(ledgerPath, readOnly: true);
        return ledger.ListAll().Select(IngestHistoryRow.From).ToList();
    }
}
