using Airshelf.Core.Model;

namespace Airshelf.Core.Ledger;

/// <summary>
/// ファイルの内容フィンガープリント(サイズ + 最終更新)。同じパスで内容が変わったかの判定に使う
/// (取込失敗の de-dup。ADR-0011)。永続キーの一部なので永続層(Ledger)に置く。安定判定で既に
/// 取得済みの値から作れるので追加 I/O は無い。
/// </summary>
public readonly record struct FileFingerprint(long SizeBytes, long LastWriteUtcTicks)
{
    public static FileFingerprint Of(RecordingFile file) =>
        new(file.SizeBytes, file.LastWriteTimeUtc.UtcTicks);
}

/// <summary>
/// 失敗のストアの 1 件(要対応の「解決していない取込失敗」。ADR-0018 / #172)。
/// </summary>
/// <param name="SourcePath">失敗した録音ファイルの取込元のパス。</param>
/// <param name="Reason">失敗の理由(トーストと同じ整形済みの文言)。理由を持つ前(#172 の移行前)に記録した行は null。</param>
/// <param name="FailedAtUtc">記録した時刻(同じパスで内容が変わって記録し直したら更新)。#172 の移行前に記録した行は null。</param>
public sealed record IngestFailureRecord(string SourcePath, string? Reason, DateTimeOffset? FailedAtUtc);

/// <summary>
/// 取込失敗の de-dup ストア(ADR-0011)。**通知済み**の失敗(失敗シグナルの発行に成功したもの)を永続記録し、
/// 同じ失敗の再通知を抑える。鍵はソースパス + フィンガープリント(内容変化で再通知)。常駐の再起動を
/// 跨いで永続する(さもないと再起動ごとに standing な失敗を全部再通知する)。永続実装は <see cref="SqliteLedger"/>。
/// 管理画面の要対応(ADR-0018)は、ここにある失敗を「解決していない取込失敗」として並べる(<see cref="List"/>)。
/// </summary>
public interface IIngestFailureStore
{
    /// <summary>この (パス, フィンガープリント) が既に通知済みか。未知(新規)なら false。</summary>
    bool IsKnown(string sourcePath, FileFingerprint fingerprint);

    /// <summary>失敗を通知済みとして記録する(既存パスはフィンガープリント・理由・時刻を置き換える)。</summary>
    /// <param name="reason">失敗の理由(トーストと同じ整形済みの文言)。</param>
    /// <param name="failedAtUtc">記録する時刻。</param>
    void Record(string sourcePath, FileFingerprint fingerprint, string reason, DateTimeOffset failedAtUtc);

    /// <summary>
    /// そのパスの失敗記録を消す(取込成功時・取込元から消えたとき。将来また失敗したら再通知される)。
    /// </summary>
    void Clear(string sourcePath);

    /// <summary>記録している失敗の全件(順序は決めない)。</summary>
    IReadOnlyList<IngestFailureRecord> List();
}

/// <summary>何もしないストア(dry-run / 非 notify 経路。常に未知扱いで、記録も消去もしない)。</summary>
public sealed class NullIngestFailureStore : IIngestFailureStore
{
    public bool IsKnown(string sourcePath, FileFingerprint fingerprint) => false;
    public void Record(string sourcePath, FileFingerprint fingerprint, string reason, DateTimeOffset failedAtUtc) { }
    public void Clear(string sourcePath) { }
    public IReadOnlyList<IngestFailureRecord> List() => [];
}
