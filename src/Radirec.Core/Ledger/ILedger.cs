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

    /// <summary>
    /// 同一「内容」が同じ放送スロット(放送日 + 放送時刻)で取り込み済みか。取込元パス/番組フォルダ名に
    /// 非依存で判定するため、ソースフォルダのリネームや台帳喪失後でも重複取込を防ぐ(#79 / ADR-0014)。
    /// 内容の同一性は <paramref name="fingerprint"/>(サイズ + 最終更新)で近似する。放送日/時刻で裏取り
    /// するのは、異なる放送が偶然同サイズ + 同更新時刻で衝突したときの誤スキップ(取りこぼし)を防ぐため。
    /// パート序数は統合フォルダ順で変わりうる(まさにリネームで変わる値)ためキーに含めない。
    /// </summary>
    bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime);

    /// <summary>取り込み済みとして記録する(既存キーは無視)。</summary>
    void Record(LedgerEntry entry);

    /// <summary>
    /// 取り込み済みの全記録を取込日時の降順(新しい順)で返す(取込履歴ビュー用。S4)。
    /// 読み取り専用の用途で、書き込み側とは独立の接続から呼ばれうる。使用不能な台帳(dry-run の
    /// NullLedger・互換スキーマでない read-only DB)は空を返す。
    /// </summary>
    IReadOnlyList<LedgerEntry> ListAll();
}
