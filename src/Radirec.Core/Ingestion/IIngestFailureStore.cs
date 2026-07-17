namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込失敗の de-dup ストア(ADR-0011)。**通知済み**の失敗(1002 発行に成功したもの)を永続記録し、
/// 同じ失敗の再通知を抑える。鍵はソースパス + フィンガープリント(内容変化で再通知)。サービス再起動を
/// 跨いで永続する(さもないと再起動ごとに standing な失敗を全部再通知する)。
/// </summary>
public interface IIngestFailureStore
{
    /// <summary>この (パス, フィンガープリント) が既に通知済みか。未知(新規)なら false。</summary>
    bool IsKnown(string sourcePath, FileFingerprint fingerprint);

    /// <summary>失敗を通知済みとして記録する(既存パスはフィンガープリントを更新)。</summary>
    void Record(string sourcePath, FileFingerprint fingerprint);

    /// <summary>そのパスの失敗記録を消す(取込成功時。将来また失敗したら再通知される)。</summary>
    void Clear(string sourcePath);
}

/// <summary>何もしないストア(dry-run / 非 notify 経路。常に未知扱いで、記録も消去もしない)。</summary>
public sealed class NullIngestFailureStore : IIngestFailureStore
{
    public bool IsKnown(string sourcePath, FileFingerprint fingerprint) => false;
    public void Record(string sourcePath, FileFingerprint fingerprint) { }
    public void Clear(string sourcePath) { }
}
