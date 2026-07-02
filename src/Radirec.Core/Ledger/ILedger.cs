using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>取り込み済みを記録する冪等性ストア(ローカルディスク)。</summary>
public interface ILedger
{
    /// <summary>同一性キーが取り込み済みか。</summary>
    bool Contains(IdentityKey key);

    /// <summary>同一ソースパスが取り込み済みか(フォールバックキーの重複コピー抑止)。</summary>
    bool ContainsSource(string sourcePath);

    /// <summary>取り込み済みとして記録する(既存キーは無視)。</summary>
    void Record(LedgerEntry entry);
}
