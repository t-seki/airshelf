using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>
/// 何も記録しない台帳。dry-run で台帳 DB がまだ存在しない場合に使う(全て未取り込み扱い)。
/// </summary>
public sealed class NullLedger : ILedger
{
    public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex) => false;

    public bool ContainsSource(string sourcePath) => false;

    public bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime) => false;

    public void Record(LedgerEntry entry)
        => throw new InvalidOperationException("NullLedger には記録できません(dry-run 用)。");

    public IReadOnlyList<LedgerEntry> ListAll() => [];
}
