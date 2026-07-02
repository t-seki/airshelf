using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>取り込み済みを記録する冪等性ストア(ローカルディスク)。</summary>
public interface ILedger
{
    /// <summary>
    /// 同一性キー(局・番組・日)+ 放送時刻が取り込み済みか。同一キーでも時刻が異なれば別放送として
    /// 未取込扱い。時刻なし(<paramref name="broadcastTime"/> = null)は同日どの時刻とも一致する
    /// ワイルドカード(cross-source の同一回を重複排除。ADR-0002)。
    /// </summary>
    bool Contains(IdentityKey key, TimeOnly? broadcastTime);

    /// <summary>同一ソースパスが取り込み済みか(フォールバックキーの重複コピー抑止)。</summary>
    bool ContainsSource(string sourcePath);

    /// <summary>取り込み済みとして記録する(既存キーは無視)。</summary>
    void Record(LedgerEntry entry);
}
