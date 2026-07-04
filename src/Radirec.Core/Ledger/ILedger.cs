using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>取り込み済みを記録する冪等性ストア(ローカルディスク)。</summary>
public interface ILedger
{
    /// <summary>
    /// 同一性キー(局・番組・日)+ 放送時刻 + パート序数が取り込み済みか(厳密一致)。同一キーでも
    /// 時刻 or part が異なれば別放送として未取込扱い。時刻なしソース(radikool)の同日重複は
    /// <paramref name="partIndex"/> で区別する(ADR-0004。ワイルドカードは撤去済み)。
    /// </summary>
    bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex);

    /// <summary>同一ソースパスが取り込み済みか(フォールバックキーの重複コピー抑止)。</summary>
    bool ContainsSource(string sourcePath);

    /// <summary>取り込み済みとして記録する(既存キーは無視)。</summary>
    void Record(LedgerEntry entry);
}
